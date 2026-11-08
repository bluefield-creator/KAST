using System.Diagnostics;
using System.Runtime.InteropServices;
using KAST.Core.Enums;
using KAST.Core.Events;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using KAST.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KAST.Infrastructure.Services;

/// <summary>
/// Server instance CRUD and process lifecycle.
/// <para>
/// Every operation runs on its own short-lived <see cref="KastDbContext"/>
/// when a context factory is available. The service is resolved per Blazor
/// circuit, so a shared context lives for hours: it hands back stale tracked
/// rows (a stop then kills a PID that is long gone and orphans the real one)
/// and is used concurrently by hub callbacks ("A second operation was started
/// on this context instance"), which aborted lifecycle writes half-way and
/// stranded instances in Starting/Stopping.
/// </para>
/// <para>
/// Lifecycle changes (start/stop/restart/exit handling/reaping) are serialized
/// per instance through <see cref="InstanceLifecycleLocks"/>.
/// </para>
/// </summary>
public class ServerInstanceService(
    KastDbContext scopedDb,
    IProcessManagerService processManager,
    IAppEventBroadcaster broadcaster,
    ILogger<ServerInstanceService> logger,
    IOutputSanitizer sanitizer,
    IServiceScopeFactory scopeFactory,
    IServerConsoleLogTailer? consoleLogTailer = null,
    IHostEnvironment? hostEnvironment = null,
    IServerConfigService? serverConfigService = null,
    IDbContextFactory<KastDbContext>? contextFactory = null) : IServerInstanceService
{
    /// <summary>How long the exit handler waits for an in-flight stop/restart to finish.</summary>
    private static readonly TimeSpan ExitHandlerLockTimeout = TimeSpan.FromMinutes(2);

    // ── DbContext lease ──────────────────────────────────────────────────────

    private DbLease OpenDb()
        => contextFactory is null
            ? new DbLease(scopedDb, owned: false)
            : new DbLease(contextFactory.CreateDbContext(), owned: true);

    private readonly struct DbLease(KastDbContext context, bool owned) : IAsyncDisposable
    {
        public KastDbContext Db => context;

        public ValueTask DisposeAsync() => owned ? context.DisposeAsync() : ValueTask.CompletedTask;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServerInstance>> GetAllInstancesAsync(CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        return await lease.Db.ServerInstances
            .Include(s => s.Mods).ThenInclude(m => m.SteamMod)
            .Include(s => s.HeadlessClients)
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ServerInstance>> GetLightInstancesAsync(CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        return await lease.Db.ServerInstances
            .AsNoTracking()
            .Where(s => s.Status != ServerInstanceStatus.Stopped)
            .OrderBy(s => s.Name)
            .Select(s => new ServerInstance { Id = s.Id, Name = s.Name, Status = s.Status, Port = s.Port })
            .ToListAsync(ct);
    }

    public async Task<ServerInstance?> GetInstanceByIdAsync(int id, CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        return await lease.Db.ServerInstances
            .Include(s => s.Mods).ThenInclude(m => m.SteamMod)
            .Include(s => s.HeadlessClients)
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    public async Task<ServerInstance> CreateInstanceAsync(ServerInstance instance, CancellationToken ct = default)
    {
        using var activity = KastActivitySources.Instances.StartActivity(
            "kast.instance.create", ActivityKind.Internal);
        activity?.SetTag("instance.name", instance.Name);
        activity?.SetTag("instance.port", instance.Port);

        instance.CreatedAt = DateTime.UtcNow;

        // Clear SteamMod navigations — EF only needs the FK (SteamModId) to write the join rows.
        foreach (var sim in instance.Mods)
            sim.SteamMod = null!;

        await using var lease = OpenDb();
        var db = lease.Db;
        db.ServerInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        await ReconcileHeadlessClientsAsync(db, instance.Id, instance.HeadlessClientCount, ct);

        // Do not hand a tracked graph from a disposed context back to the UI.
        if (contextFactory is not null)
            db.ChangeTracker.Clear();

        activity?.SetTag("instance.id", instance.Id);
        return instance;
    }

    public async Task<ServerInstance> UpdateInstanceAsync(ServerInstance instance, CancellationToken ct = default)
    {
        instance.LastModified = DateTime.UtcNow;

        await using var lease = OpenDb();
        var db = lease.Db;

        // Use ExecuteUpdateAsync to update only scalar properties via a direct SQL UPDATE.
        // This avoids EF walking the navigation graph (Mods, HeadlessClients).
        await db.ServerInstances
            .Where(s => s.Id == instance.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, instance.Name)
                .SetProperty(x => x.InstallPath, instance.InstallPath)
                .SetProperty(x => x.Port, instance.Port)
                .SetProperty(x => x.SteamQueryPort, instance.SteamQueryPort)
                .SetProperty(x => x.RestartPolicy, instance.RestartPolicy)
                .SetProperty(x => x.MaxRestartAttempts, instance.MaxRestartAttempts)
                .SetProperty(x => x.AutoStartTime, instance.AutoStartTime)
                .SetProperty(x => x.AutoStopTime, instance.AutoStopTime)
                .SetProperty(x => x.ScheduleEnabled, instance.ScheduleEnabled)
                .SetProperty(x => x.ServerCfgContent, instance.ServerCfgContent)
                .SetProperty(x => x.BasicCfgContent, instance.BasicCfgContent)
                .SetProperty(x => x.ArmaProfileContent, instance.ArmaProfileContent)
                .SetProperty(x => x.AdditionalParameters, instance.AdditionalParameters)
                .SetProperty(x => x.ContactDlc, instance.ContactDlc)
                .SetProperty(x => x.GmDlc, instance.GmDlc)
                .SetProperty(x => x.PfDlc, instance.PfDlc)
                .SetProperty(x => x.CslaDlc, instance.CslaDlc)
                .SetProperty(x => x.WsDlc, instance.WsDlc)
                .SetProperty(x => x.SpeDlc, instance.SpeDlc)
                .SetProperty(x => x.RfDlc, instance.RfDlc)
                .SetProperty(x => x.EfDlc, instance.EfDlc)
                .SetProperty(x => x.EnableHT, instance.EnableHT)
                .SetProperty(x => x.MaxMemOverride, instance.MaxMemOverride)
                .SetProperty(x => x.MaxMem, instance.MaxMem)
                .SetProperty(x => x.CpuCountOverride, instance.CpuCountOverride)
                .SetProperty(x => x.CpuCount, instance.CpuCount)
                .SetProperty(x => x.EnableRanking, instance.EnableRanking)
                .SetProperty(x => x.HeadlessClientCount, instance.HeadlessClientCount)
                .SetProperty(x => x.HttpDownloadsEnabled, instance.HttpDownloadsEnabled)
                .SetProperty(x => x.LastModified, instance.LastModified),
            ct);

        await ReconcileHeadlessClientsAsync(db, instance.Id, instance.HeadlessClientCount, ct);

        return instance;
    }

    /// <summary>
    /// Keeps <c>HeadlessClients</c> rows in sync with the scalar
    /// <c>HeadlessClientCount</c>. Running clients are never removed when the count is lowered.
    /// </summary>
    private async Task ReconcileHeadlessClientsAsync(KastDbContext db, int instanceId, int targetCount, CancellationToken ct)
    {
        targetCount = Math.Max(0, targetCount);

        var existing = await db.HeadlessClients
            .Where(h => h.ServerInstanceId == instanceId)
            .OrderBy(h => h.Id)
            .ToListAsync(ct);

        var changed = false;

        for (var i = existing.Count; i < targetCount; i++)
        {
            db.HeadlessClients.Add(new HeadlessClient { ServerInstanceId = instanceId });
            changed = true;
        }

        if (existing.Count > targetCount)
        {
            var excess = existing.Skip(targetCount).ToList();
            var running = excess.Where(h => h.ProcessId.HasValue).ToList();
            if (running.Count > 0)
                logger.LogWarning(
                    "Cannot remove {Count} running headless client(s) from instance {InstanceId}; lower the count after they stop",
                    running.Count, instanceId);

            var removable = excess.Where(h => !h.ProcessId.HasValue).ToList();
            if (removable.Count > 0)
            {
                db.HeadlessClients.RemoveRange(removable);
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    public async Task DeleteInstanceAsync(int id, bool deleteFiles = false, CancellationToken ct = default)
    {
        ServerInstance? snapshot;
        await using (var probe = OpenDb())
        {
            snapshot = await probe.Db.ServerInstances
                .Include(s => s.HeadlessClients)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
        }

        if (snapshot == null)
            return;

        using var activity = KastActivitySources.Instances.StartActivity(
            "kast.instance.delete", ActivityKind.Internal);
        activity?.SetTag("instance.id", id);
        activity?.SetTag("instance.name", snapshot.Name);

        // Anything that may still own a process must be stopped first — deleting
        // the row would otherwise orphan the server with nobody tracking it.
        var mayOwnProcess = snapshot.ProcessId.HasValue
            || snapshot.HeadlessClients.Any(h => h.ProcessId.HasValue)
            || snapshot.Status is not (ServerInstanceStatus.Stopped or ServerInstanceStatus.Crashed);
        if (mayOwnProcess)
            await StopInstanceAsync(id, ct);
        else if (consoleLogTailer is not null)
            await consoleLogTailer.StopFollowingAsync(id);

        await using var lease = OpenDb();
        var db = lease.Db;
        var instance = await db.ServerInstances
            .Include(s => s.Mods).ThenInclude(m => m.SteamMod)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (instance == null)
            return;

        // Capture install path & check whether it's shared with other instances
        // BEFORE we drop this row, so explicit file deletion cannot wipe shared data.
        var installPath = instance.InstallPath;
        var isInstallPathShared = !string.IsNullOrEmpty(installPath)
            && await db.ServerInstances
                .AnyAsync(s => s.Id != id && s.InstallPath == installPath, ct);

        activity?.SetTag("instance.path_shared", isInstallPathShared);
        db.ServerInstances.Remove(instance);
        await db.SaveChangesAsync(ct);

        // Best-effort filesystem cleanup — never let a stray I/O failure
        // resurrect the row we just deleted from the DB.
        try
        {
            DeleteInstanceFiles(instance, wipeInstallPath: deleteFiles && !isInstallPathShared);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            logger.LogError(ex,
                "Failed to clean up files for deleted instance {Id} ({Name})",
                id, instance.Name);
        }
    }

    /// <summary>
    /// Removes on-disk artefacts owned by the instance: per-instance config dir,
    /// mod symlinks created for this instance, and optionally the whole install
    /// directory when no other instance shares it.
    /// </summary>
    private void DeleteInstanceFiles(ServerInstance instance, bool wipeInstallPath)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath))
            return;

        var configDir = GetInstanceConfigDirectory(instance);
        TryDeleteDirectory(configDir, recursive: true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            CleanupLinuxProfileSymlink(instance);

        var modsDir = GetInstanceModsDirectory(instance);
        if (Directory.Exists(modsDir))
        {
            foreach (var modLink in instance.Mods)
            {
                var mod = modLink.SteamMod;
                if (mod == null || string.IsNullOrEmpty(mod.Name))
                    continue;

                var linkPath = Path.Combine(modsDir, $"@{SanitizeModName(mod.Name)}");
                if (!Path.Exists(linkPath))
                    continue;

                try
                {
                    // Only delete if it's actually a symlink we created.
                    if (Directory.ResolveLinkTarget(linkPath, false) != null)
                        Directory.Delete(linkPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove mod symlink for instance {Id}", instance.Id);
                }
            }
        }

        if (wipeInstallPath)
        {
            TryDeleteDirectory(GetInstanceInstallDirectory(instance), recursive: true);
            logger.LogInformation("Wiped install directory for instance {Id}", instance.Id);
        }
        else
        {
            logger.LogInformation("Kept install directory shared with other instances for instance {Id}", instance.Id);
        }
    }

    private void TryDeleteDirectory(string path, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete instance directory");
        }
    }

    // ── Start ────────────────────────────────────────────────────────────────

    public async Task StartInstanceAsync(int id, CancellationToken ct = default)
    {
        using (await InstanceLifecycleLocks.EnterAsync(id, ct))
            await StartInstanceCoreAsync(id, ct);
    }

    private async Task StartInstanceCoreAsync(int id, CancellationToken ct)
    {
        await using var lease = OpenDb();
        var db = lease.Db;

        var instance = await db.ServerInstances
            .Include(s => s.Mods).ThenInclude(m => m.SteamMod)
            .Include(s => s.HeadlessClients)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KAST.Core.Exceptions.NotFoundException($"Server instance {id} not found");

        if (instance.Status == ServerInstanceStatus.Downloading)
            throw new InvalidOperationException(
                $"Server instance {id} is being installed or updated. Wait for the install to finish before starting it.");

        if (instance.ProcessId is { } existingPid && processManager.IsProcessRunning(existingPid))
        {
            // Already up. If the row was stranded in a transitional state, heal it.
            if (instance.Status != ServerInstanceStatus.Running)
            {
                logger.LogWarning("Server {Name} was {Status} while its process {Pid} is alive; marking Running",
                    instance.Name, instance.Status, existingPid);
                instance.Status = ServerInstanceStatus.Running;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastStatusAsync(instance.Id, instance.Status);
            }
            return;
        }

        if (instance.Status is ServerInstanceStatus.Running
            or ServerInstanceStatus.Starting
            or ServerInstanceStatus.Stopping
            or ServerInstanceStatus.Restarting)
        {
            // We hold the lifecycle lock, so no operation is in flight: the row
            // is stale (process gone). Close it out and start fresh rather than
            // silently refusing — that refusal is what left admins unable to
            // start a server until the panel was restarted.
            logger.LogWarning("Server {Name} was {Status} but its process is gone; starting fresh",
                instance.Name, instance.Status);
            if (instance.ProcessId is { } stalePid)
                await CloseProcessHistoryEntryAsync(db, id, stalePid, "Lost", ct: CancellationToken.None);
            instance.ProcessId = null;
            instance.StartedAt = null;
        }

        using var activity = KastActivitySources.Instances.StartActivity(
            "kast.instance.start", ActivityKind.Internal);
        activity?.SetTag("instance.id", id);
        activity?.SetTag("instance.name", instance.Name);
        activity?.SetTag("instance.port", instance.Port);

        // Anything still carrying this instance's tag is a leftover from an
        // earlier run (panel crash, lost PID) and would hold the game port.
        await KillStrayProcessesAsync(instance, new HashSet<int>(), "before start");
        await StopLeftoverHeadlessClientsAsync(instance);

        await LinkModsAsync(db, instance.Id, ct);
        activity?.AddEvent(new ActivityEvent("instance.mods_linked"));

        WriteConfigFiles(instance);
        activity?.AddEvent(new ActivityEvent("instance.config_written"));

        instance.Status = ServerInstanceStatus.Starting;
        await db.SaveChangesAsync(ct);
        await BroadcastStatusAsync(instance.Id, instance.Status);

        var sessionStartedUtc = DateTime.UtcNow;
        var logChannel = CreateLogPump(id);
        void OnOutputLine(int _, string line) => logChannel.Writer.TryWrite(new LogEntryEvent(id, line, DateTime.UtcNow));
        void OnProcessExited(int pid, int exitCode) => HandleServerExit(id, pid, exitCode, logChannel.Writer);
        void OnHeadlessClientExited(int pid, int exitCode) => HandleHeadlessClientExit(id, pid, exitCode);

        string executable;
        int serverPid;
        try
        {
            executable = GetServerExecutable(instance);
            var args = BuildLaunchArguments(instance);
            activity?.SetTag("instance.executable", sanitizer.ToDisplayPath(executable));
            logger.LogInformation("Starting server {Name}", instance.Name);

            serverPid = await processManager.StartServerProcessAsync(executable, args, OnOutputLine, OnProcessExited, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start server instance {Id}", id);
            RecordException(activity, ex);
            logChannel.Writer.TryComplete();

            // The process never got a PID, so it never ran: Stopped, not Crashed.
            instance.Status = ServerInstanceStatus.Stopped;
            instance.StartedAt = null;
            await PersistFinalStateAsync(db, instance);
            throw;
        }

        // ── The server is running. From here on nothing may abandon it: the
        // caller's token is ignored and every failure is contained, otherwise
        // the PID is lost and the process becomes an orphan.
        instance.ProcessId = serverPid;
        instance.Status = ServerInstanceStatus.Running;
        instance.StartedAt = DateTime.UtcNow;
        db.ServerInstanceProcessHistories.Add(new ServerInstanceProcessHistory
        {
            ServerInstanceId = instance.Id,
            ProcessId = serverPid,
            StartedAt = instance.StartedAt.Value
        });
        await TrySaveAsync(db, $"record PID {serverPid} for instance {id}");

        activity?.SetTag("instance.pid", serverPid);
        activity?.AddEvent(new ActivityEvent("instance.process_started", tags: new ActivityTagsCollection { ["pid"] = serverPid }));

        try
        {
            consoleLogTailer?.StartFollowing(instance, sessionStartedUtc, replayExistingContent: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not follow the RPT log for instance {Id}", id);
        }

        var hcIndex = 0;
        foreach (var hc in instance.HeadlessClients.OrderBy(h => h.Id))
        {
            hcIndex++;
            try
            {
                var hcArgs = BuildHeadlessClientArguments(instance, hcIndex);
                var hcPid = await processManager.StartServerProcessAsync(executable, hcArgs, OnOutputLine, OnHeadlessClientExited, CancellationToken.None);
                hc.ProcessId = hcPid;
                hc.Status = ServerInstanceStatus.Running;
                hc.StartedAt = DateTime.UtcNow;
                activity?.AddEvent(new ActivityEvent("instance.headless_client_started", tags: new ActivityTagsCollection { ["hc.pid"] = hcPid }));
            }
            catch (Exception ex)
            {
                // A headless client failing must not take the server down with it.
                logger.LogError(ex, "Failed to start headless client {Index} for instance {Id}", hcIndex, id);
                hc.ProcessId = null;
                hc.Status = ServerInstanceStatus.Crashed;
                hc.StartedAt = null;
            }
        }

        await PersistFinalStateAsync(db, instance);
    }

    /// <summary>
    /// Console output is funnelled through a bounded channel: a chatty Arma
    /// server can emit thousands of lines/second. Under burst the oldest lines
    /// are dropped rather than growing without bound.
    /// </summary>
    private System.Threading.Channels.Channel<LogEntryEvent> CreateLogPump(int instanceId)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<LogEntryEvent>(
            new System.Threading.Channels.BoundedChannelOptions(2000)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _ = Task.Run(async () =>
        {
            try
            {
                var reader = channel.Reader;
                while (await reader.WaitToReadAsync(CancellationToken.None))
                {
                    var drained = 0;
                    while (drained < 50 && reader.TryRead(out var entry))
                    {
                        try
                        {
                            await broadcaster.BroadcastLogEntryAsync(entry);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Failed to broadcast log entry for instance {InstanceId}", instanceId);
                        }

                        drained++;
                    }

                    if (drained == 50)
                        await Task.Delay(50, CancellationToken.None); // burst — yield between batches
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Console pump for instance {InstanceId} stopped", instanceId);
            }
        }, CancellationToken.None);

        return channel;
    }

    /// <summary>
    /// Runs when the server process exits, for whatever reason. Waits for any
    /// in-flight stop/restart so it never races it; only touches the row while
    /// it still refers to this PID.
    /// </summary>
    private void HandleServerExit(int id, int pid, int exitCode,
        System.Threading.Channels.ChannelWriter<LogEntryEvent> logWriter)
    {
        _ = Task.Run(async () =>
        {
            IDisposable? gate = null;
            try
            {
                gate = await InstanceLifecycleLocks.TryEnterAsync(id, ExitHandlerLockTimeout);
                if (gate is null)
                    logger.LogWarning("Exit handler for instance {Id} timed out waiting for a lifecycle operation; continuing", id);

                await using var exitScope = scopeFactory.CreateAsyncScope();
                var exitDb = exitScope.ServiceProvider.GetRequiredService<KastDbContext>();

                var instanceRow = await exitDb.ServerInstances
                    .Include(s => s.HeadlessClients)
                    .FirstOrDefaultAsync(s => s.Id == id, CancellationToken.None);
                var unexpected = instanceRow is not null && instanceRow.ProcessId == pid;

                // An exit the panel did not ask for: record it as Exited/Crashed.
                // Intentional stops already closed the entry with "Stopped".
                var reason = unexpected ? (exitCode == 0 ? "Exited" : "Crashed") : "Stopped";
                await CloseProcessHistoryEntryAsync(exitDb, id, pid, reason, exitCode, CancellationToken.None);

                ServerInstanceStatus? newStatus = null;
                if (unexpected)
                {
                    newStatus = exitCode == 0 ? ServerInstanceStatus.Stopped : ServerInstanceStatus.Crashed;
                    instanceRow!.Status = newStatus.Value;
                    instanceRow.ProcessId = null;
                    instanceRow.StartedAt = null;

                    // Headless clients of a dead server would retry the
                    // connection forever as orphans.
                    foreach (var hc in instanceRow.HeadlessClients.Where(h => h.ProcessId.HasValue))
                    {
                        try { await processManager.KillProcessAsync(hc.ProcessId!.Value, CancellationToken.None); }
                        catch (Exception ex) { logger.LogWarning(ex, "Failed to kill headless client {Pid} of exited server {Id}", hc.ProcessId, id); }
                        hc.ProcessId = null;
                        hc.Status = ServerInstanceStatus.Stopped;
                        hc.StartedAt = null;
                    }

                    logger.LogWarning("Server instance {Id} (PID {Pid}) exited unexpectedly with code {Code}", id, pid, exitCode);
                }

                await exitDb.SaveChangesAsync(CancellationToken.None);

                if (newStatus is { } status)
                {
                    await BroadcastStatusAsync(id, status);
                    if (consoleLogTailer is not null)
                        await consoleLogTailer.StopFollowingAsync(id);
                }

                await broadcaster.BroadcastLogEntryAsync(
                    new LogEntryEvent(id, $"Process exited with code {exitCode}", DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling process exit for instance {Id}", id);
            }
            finally
            {
                gate?.Dispose();
                logWriter.TryComplete();
            }
        }, CancellationToken.None);
    }

    private void HandleHeadlessClientExit(int id, int pid, int exitCode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var hcScope = scopeFactory.CreateAsyncScope();
                var hcDb = hcScope.ServiceProvider.GetRequiredService<KastDbContext>();
                var hcRow = await hcDb.HeadlessClients
                    .FirstOrDefaultAsync(h => h.ServerInstanceId == id && h.ProcessId == pid, CancellationToken.None);
                if (hcRow is not null)
                {
                    hcRow.Status = exitCode == 0 ? ServerInstanceStatus.Stopped : ServerInstanceStatus.Crashed;
                    hcRow.ProcessId = null;
                    hcRow.StartedAt = null;
                    await hcDb.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling headless client exit for instance {Id}", id);
            }
        }, CancellationToken.None);
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    public async Task StopInstanceAsync(int id, CancellationToken ct = default)
    {
        using (await InstanceLifecycleLocks.EnterAsync(id, ct))
            await StopInstanceCoreAsync(id, ct);
    }

    private async Task StopInstanceCoreAsync(int id, CancellationToken ct)
    {
        await using var lease = OpenDb();
        var db = lease.Db;

        var instance = await db.ServerInstances
            .Include(s => s.HeadlessClients)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KAST.Core.Exceptions.NotFoundException($"Server instance {id} not found");

        using var activity = KastActivitySources.Instances.StartActivity(
            "kast.instance.stop", ActivityKind.Internal);
        activity?.SetTag("instance.id", id);
        activity?.SetTag("instance.name", instance.Name);
        if (instance.ProcessId.HasValue)
            activity?.SetTag("instance.pid", instance.ProcessId.Value);

        instance.Status = ServerInstanceStatus.Stopping;
        await db.SaveChangesAsync(ct);
        await BroadcastStatusAsync(instance.Id, instance.Status);

        // ── Point of no return. The caller's token is deliberately ignored from
        // here: a closed browser tab or aborted HTTP request used to cancel the
        // stop half-way and strand the instance in Stopping forever.
        var stopFailures = new List<Exception>();
        var serverStillRunning = false;
        try
        {
            foreach (var hc in instance.HeadlessClients.Where(h => h.ProcessId.HasValue))
            {
                var pid = hc.ProcessId!.Value;
                if (await TryStopProcessAsync(pid, "headless client", id, stopFailures))
                {
                    hc.ProcessId = null;
                    hc.Status = ServerInstanceStatus.Stopped;
                    hc.StartedAt = null;
                    activity?.AddEvent(new ActivityEvent("instance.headless_client_stopped"));
                }
                else
                {
                    hc.Status = ServerInstanceStatus.Running;
                }
            }

            if (instance.ProcessId is { } serverPid)
                serverStillRunning = !await TryStopProcessAsync(serverPid, "server", id, stopFailures);

            // Process hunting: anything else tagged for this instance (a lost
            // PID, a duplicate launch) would keep the port busy.
            var tracked = new HashSet<int>(instance.HeadlessClients.Where(h => h.ProcessId.HasValue).Select(h => h.ProcessId!.Value));
            if (instance.ProcessId is { } trackedPid)
                tracked.Add(trackedPid);
            await KillStrayProcessesAsync(instance, tracked, "during stop");
        }
        finally
        {
            if (!serverStillRunning)
            {
                if (instance.ProcessId.HasValue)
                    await CloseProcessHistoryEntryAsync(db, id, instance.ProcessId.Value, "Stopped", ct: CancellationToken.None);

                instance.ProcessId = null;
                instance.Status = ServerInstanceStatus.Stopped;
                instance.StartedAt = null;
                if (consoleLogTailer is not null)
                {
                    try { await consoleLogTailer.StopFollowingAsync(id); }
                    catch (Exception ex) { logger.LogDebug(ex, "Failed to stop RPT tailer for instance {Id}", id); }
                }
            }
            else
            {
                instance.Status = ServerInstanceStatus.Running;
            }

            await PersistFinalStateAsync(db, instance);
        }

        if (stopFailures.Count > 0)
            throw new InvalidOperationException(
                $"Failed to stop server instance {id}. One or more processes are still running.",
                stopFailures[0]);
    }

    /// <summary>Stops one process. Returns true when it is gone afterwards.</summary>
    private async Task<bool> TryStopProcessAsync(int pid, string kind, int instanceId, List<Exception> failures)
    {
        try
        {
            await processManager.StopProcessAsync(pid, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop {Kind} process {Pid} for instance {Id}", kind, pid, instanceId);
            if (!processManager.IsProcessRunning(pid))
                return true;

            failures.Add(ex);
            return false;
        }
    }

    // ── Restart ──────────────────────────────────────────────────────────────

    public async Task RestartInstanceAsync(int id, CancellationToken ct = default)
    {
        using var gate = await InstanceLifecycleLocks.EnterAsync(id, ct);

        using var activity = KastActivitySources.Instances.StartActivity(
            "kast.instance.restart", ActivityKind.Internal);
        activity?.SetTag("instance.id", id);

        await using (var lease = OpenDb())
        {
            var instance = await lease.Db.ServerInstances.FirstOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new KAST.Core.Exceptions.NotFoundException($"Server instance {id} not found");
            activity?.SetTag("instance.name", instance.Name);

            instance.Status = ServerInstanceStatus.Restarting;
            await lease.Db.SaveChangesAsync(ct);
        }
        await BroadcastStatusAsync(id, ServerInstanceStatus.Restarting);

        try
        {
            await StopInstanceCoreAsync(id, CancellationToken.None);
            await StartInstanceCoreAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            await HealTransitionalStateAsync(id);
            throw;
        }
    }

    /// <summary>
    /// Last-resort repair after a failed lifecycle operation: a row left in
    /// Starting/Stopping/Restarting is resolved against the real process.
    /// Caller must hold the instance's lifecycle lock.
    /// </summary>
    private async Task HealTransitionalStateAsync(int id)
    {
        try
        {
            await using var lease = OpenDb();
            var instance = await lease.Db.ServerInstances.FirstOrDefaultAsync(s => s.Id == id, CancellationToken.None);
            if (instance is null || instance.Status is not (ServerInstanceStatus.Starting
                    or ServerInstanceStatus.Stopping or ServerInstanceStatus.Restarting))
                return;

            var alive = instance.ProcessId is { } pid && processManager.IsProcessRunning(pid);
            instance.Status = alive ? ServerInstanceStatus.Running : ServerInstanceStatus.Stopped;
            if (!alive)
            {
                instance.ProcessId = null;
                instance.StartedAt = null;
            }

            await lease.Db.SaveChangesAsync(CancellationToken.None);
            await BroadcastStatusAsync(id, instance.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to repair the state of instance {Id}", id);
        }
    }

    // ── Process hunting ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds Arma processes on the host that carry a KAST instance tag but are
    /// not tracked by their instance, and resolves them: a server whose row
    /// claims to be running but lost its PID is adopted; everything else is
    /// killed. Returns the number of processes acted on.
    /// </summary>
    public async Task<int> ReapOrphanedProcessesAsync(CancellationToken ct = default)
    {
        var tagged = processManager.FindKastServerProcesses() ?? [];
        if (tagged.Count == 0)
            return 0;

        var handled = 0;
        foreach (var group in tagged.GroupBy(p => p.InstanceId))
        {
            ct.ThrowIfCancellationRequested();

            // Never interfere with an operation in flight; next sweep will see it.
            using var gate = await InstanceLifecycleLocks.TryEnterAsync(group.Key, TimeSpan.Zero, ct);
            if (gate is null)
                continue;

            await using var lease = OpenDb();
            var db = lease.Db;
            var instance = await db.ServerInstances
                .Include(s => s.HeadlessClients)
                .FirstOrDefaultAsync(s => s.Id == group.Key, ct);
            if (instance is null)
            {
                foreach (var p in group)
                    logger.LogWarning("Arma process {Pid} is tagged for unknown instance {Id}; leaving it alone", p.Pid, group.Key);
                continue;
            }

            if (contextFactory is null)
                await db.Entry(instance).ReloadAsync(ct); // shared context: make sure we see the latest row

            var tracked = new HashSet<int>(instance.HeadlessClients.Where(h => h.ProcessId.HasValue).Select(h => h.ProcessId!.Value));
            var trackedServerAlive = instance.ProcessId is { } trackedPid && processManager.IsProcessRunning(trackedPid);
            if (instance.ProcessId is { } serverPid)
                tracked.Add(serverPid);

            var strays = group.Where(p => !tracked.Contains(p.Pid) && IsOwnedByInstance(p, instance)).ToList();
            if (strays.Count == 0)
                continue;

            // The row says the server is up but its PID is gone — this is the
            // real server (e.g. PID lost by an aborted write). Adopt it instead
            // of killing a live game.
            var adoptable = instance.Status is ServerInstanceStatus.Running or ServerInstanceStatus.Starting
                && !trackedServerAlive
                ? strays.Where(p => !p.IsHeadlessClient).OrderBy(p => p.Pid).FirstOrDefault()
                : null;

            if (adoptable is not null)
            {
                logger.LogWarning("Adopting untracked server process {Pid} for instance {Name}", adoptable.Pid, instance.Name);
                if (instance.ProcessId is { } lostPid)
                    await CloseProcessHistoryEntryAsync(db, instance.Id, lostPid, "Lost", ct: ct);
                instance.ProcessId = adoptable.Pid;
                instance.Status = ServerInstanceStatus.Running;
                instance.StartedAt ??= DateTime.UtcNow;
                db.ServerInstanceProcessHistories.Add(new ServerInstanceProcessHistory
                {
                    ServerInstanceId = instance.Id,
                    ProcessId = adoptable.Pid,
                    StartedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
                await BroadcastStatusAsync(instance.Id, instance.Status);
                strays.Remove(adoptable);
                handled++;
            }

            foreach (var stray in strays)
            {
                logger.LogWarning(
                    "Killing orphaned {Kind} process {Pid} of instance {Name} (status {Status})",
                    stray.IsHeadlessClient ? "headless client" : "server", stray.Pid, instance.Name, instance.Status);
                try
                {
                    if (await processManager.KillProcessAsync(stray.Pid, CancellationToken.None))
                        handled++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to kill orphaned process {Pid}", stray.Pid);
                }
            }
        }

        return handled;
    }

    /// <summary>Kills tagged processes of <paramref name="instance"/> that are not in <paramref name="trackedPids"/>.</summary>
    private async Task KillStrayProcessesAsync(ServerInstance instance, IReadOnlySet<int> trackedPids, string context)
    {
        IReadOnlyList<KastProcessInfo> tagged;
        try
        {
            tagged = processManager.FindKastServerProcesses() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Process scan failed for instance {Id}", instance.Id);
            return;
        }

        foreach (var stray in tagged.Where(p => p.InstanceId == instance.Id
                     && !trackedPids.Contains(p.Pid)
                     && IsOwnedByInstance(p, instance)))
        {
            logger.LogWarning("Killing leftover {Kind} process {Pid} of instance {Name} {Context}",
                stray.IsHeadlessClient ? "headless client" : "server", stray.Pid, instance.Name, context);
            try
            {
                await processManager.KillProcessAsync(stray.Pid, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill leftover process {Pid}", stray.Pid);
            }
        }
    }

    /// <summary>Kills headless clients still recorded from a previous run before a fresh start.</summary>
    private async Task StopLeftoverHeadlessClientsAsync(ServerInstance instance)
    {
        foreach (var hc in instance.HeadlessClients.Where(h => h.ProcessId.HasValue))
        {
            try
            {
                if (processManager.IsProcessRunning(hc.ProcessId!.Value))
                {
                    logger.LogWarning("Killing leftover headless client {Pid} of instance {Name} before start", hc.ProcessId, instance.Name);
                    await processManager.KillProcessAsync(hc.ProcessId.Value, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill leftover headless client {Pid}", hc.ProcessId);
            }

            hc.ProcessId = null;
            hc.Status = ServerInstanceStatus.Stopped;
            hc.StartedAt = null;
        }
    }

    /// <summary>
    /// A tagged process only belongs to this panel's instance when its
    /// executable lives in the instance's install directory. Unknown paths are
    /// treated as foreign — another panel on the same host may use the same tags.
    /// </summary>
    private bool IsOwnedByInstance(KastProcessInfo process, ServerInstance instance)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath) || string.IsNullOrWhiteSpace(instance.InstallPath))
            return false;

        try
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(process.ExecutablePath));
            if (exeDir is null)
                return false;

            var installDir = GetInstanceInstallDirectory(instance);
            var candidates = new List<string> { installDir };
            try
            {
                if (Directory.ResolveLinkTarget(installDir, returnFinalTarget: true) is { } target)
                    candidates.Add(target.FullName);
            }
            catch (IOException) { }

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return candidates.Any(dir => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                Path.TrimEndingDirectorySeparator(exeDir),
                comparison));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    // ── Persistence helpers ──────────────────────────────────────────────────

    private async Task PersistFinalStateAsync(KastDbContext db, ServerInstance instance)
    {
        await TrySaveAsync(db, $"persist final state for instance {instance.Id}");
        await BroadcastStatusAsync(instance.Id, instance.Status);
    }

    /// <summary>Saves with retries; lifecycle outcomes must reach the database.</summary>
    private async Task TrySaveAsync(KastDbContext db, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is not DbUpdateConcurrencyException)
            {
                logger.LogWarning(ex, "Failed to {What} (attempt {Attempt}); retrying", what, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to {What}", what);
                return;
            }
        }
    }

    private async Task BroadcastStatusAsync(int id, ServerInstanceStatus status)
    {
        try
        {
            await broadcaster.BroadcastServerStatusChangedAsync(new ServerStatusChangedEvent(id, status));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to broadcast status {Status} for instance {Id}", status, id);
        }
    }

    private void RecordException(Activity? activity, Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, sanitizer.Sanitize(ex.Message));
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = ex.GetType().Name,
            ["exception.message"] = sanitizer.Sanitize(ex.Message)
        }));
    }

    // ── Mods ─────────────────────────────────────────────────────────────────

    public async Task AddModToInstanceAsync(int instanceId, int modId, int loadOrder = 0, bool isClientSide = false, bool isServerSide = false, CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        var db = lease.Db;
        var exists = await db.ServerInstanceMods
            .AnyAsync(m => m.ServerInstanceId == instanceId && m.SteamModId == modId, ct);

        if (!exists)
        {
            db.ServerInstanceMods.Add(new ServerInstanceMod
            {
                ServerInstanceId = instanceId,
                SteamModId = modId,
                IsClientSide = isClientSide,
                IsServerSide = isServerSide,
                LoadOrder = loadOrder
            });
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task UpdateModFlagsAsync(int instanceId, int modId, bool isClientSide, bool isServerSide, CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        var link = await lease.Db.ServerInstanceMods
            .FirstOrDefaultAsync(m => m.ServerInstanceId == instanceId && m.SteamModId == modId, ct);
        if (link != null)
        {
            link.IsClientSide = isClientSide;
            link.IsServerSide = isServerSide;
            await lease.Db.SaveChangesAsync(ct);
        }
    }

    public async Task RemoveModFromInstanceAsync(int instanceId, int modId, CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        var link = await lease.Db.ServerInstanceMods
            .OrderBy(m => m.ServerInstanceId)
            .FirstOrDefaultAsync(m => m.ServerInstanceId == instanceId && m.SteamModId == modId, ct);

        if (link != null)
        {
            lease.Db.ServerInstanceMods.Remove(link);
            await lease.Db.SaveChangesAsync(ct);
        }
    }

    public async Task UpdateModLoadOrderAsync(int instanceId, int modId, int newOrder, CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        var link = await lease.Db.ServerInstanceMods
            .OrderBy(m => m.ServerInstanceId)
            .FirstOrDefaultAsync(m => m.ServerInstanceId == instanceId && m.SteamModId == modId, ct);

        if (link != null)
        {
            link.LoadOrder = newOrder;
            await lease.Db.SaveChangesAsync(ct);
        }
    }

    public async Task LinkModsAsync(int instanceId, CancellationToken ct = default)
    {
        await using var lease = OpenDb();
        await LinkModsAsync(lease.Db, instanceId, ct);
    }

    private async Task LinkModsAsync(KastDbContext db, int instanceId, CancellationToken ct)
    {
        var instance = await db.ServerInstances
            .Include(s => s.Mods).ThenInclude(m => m.SteamMod)
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(s => s.Id == instanceId, ct)
            ?? throw new KAST.Core.Exceptions.NotFoundException($"Server instance {instanceId} not found");

        using var activity = KastActivitySources.Instances.StartActivity(
            "kast.instance.link_mods", ActivityKind.Internal);
        activity?.SetTag("instance.id", instanceId);
        activity?.SetTag("instance.name", instance.Name);
        activity?.SetTag("instance.mods_total", instance.Mods.Count);

        var modsDir = GetInstanceModsDirectory(instance);
        Directory.CreateDirectory(modsDir);

        int linkedCount = 0;
        foreach (var mod in instance.Mods.Select(modLink => modLink.SteamMod))
        {
            if (string.IsNullOrEmpty(mod.LocalPath) || !Directory.Exists(mod.LocalPath))
                continue;

            var linkPath = Path.Combine(modsDir, $"@{SanitizeModName(mod.Name)}");

            if (Path.Exists(linkPath))
            {
                if (Directory.ResolveLinkTarget(linkPath, false) != null)
                    Directory.Delete(linkPath);
                else
                    continue;
            }

            Directory.CreateSymbolicLink(linkPath, mod.LocalPath);
            logger.LogInformation("Linked mod {ModName}", mod.Name);
            linkedCount++;
        }

        activity?.SetTag("instance.mods_linked", linkedCount);
    }

    private string GetServerExecutable(ServerInstance instance)
    {
        var installPath = GetInstanceInstallDirectory(instance);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(installPath, "arma3server_x64.exe");

        return Path.Combine(installPath, "arma3server_x64");
    }

    private string GetInstanceInstallDirectory(ServerInstance instance)
    {
        var installPath = Environment.ExpandEnvironmentVariables(instance.InstallPath);
        return Path.IsPathFullyQualified(installPath)
            ? Path.GetFullPath(installPath)
            : Path.GetFullPath(installPath, hostEnvironment?.ContentRootPath ?? AppContext.BaseDirectory);
    }

    private string GetInstanceConfigDirectory(ServerInstance instance)
        => Path.Combine(GetInstanceInstallDirectory(instance), "KAST", instance.Id.ToString());

    private string GetInstanceModsDirectory(ServerInstance instance)
        => Path.Combine(GetInstanceInstallDirectory(instance), "mods");

    public void WriteConfigFiles(ServerInstance instance)
    {
        if (!string.IsNullOrEmpty(instance.InstallPath))
            Directory.CreateDirectory(GetInstanceInstallDirectory(instance));

        // Per-instance config directory
        var configDir = GetInstanceConfigDirectory(instance);
        Directory.CreateDirectory(configDir);

        try
        {
            if (instance.ServerCfgContent != null)
            {
                var path = Path.Join(configDir, "server.cfg");
                File.WriteAllText(path, instance.ServerCfgContent);
            }

            if (instance.BasicCfgContent != null)
            {
                var path = Path.Join(configDir, "basic.cfg");
                File.WriteAllText(path, instance.BasicCfgContent);
            }

            if (instance.ArmaProfileContent != null)
            {
                var profileName = $"server_{instance.Id}";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Read via the "Arma 3 - Other Profiles" symlink as <configDir>/<name>.Arma3Profile
                    var path = Path.Join(configDir, $"{profileName}.Arma3Profile");
                    File.WriteAllText(path, instance.ArmaProfileContent);
                }
                else
                {
                    // With -profiles=<configDir> -name=<name> the server reads
                    // <configDir>\Users\<name>\<name>.Arma3Profile, not the profiles root.
                    var profileDir = Path.Join(configDir, "Users", profileName);
                    Directory.CreateDirectory(profileDir);
                    File.WriteAllText(Path.Join(profileDir, $"{profileName}.Arma3Profile"), instance.ArmaProfileContent);

                    var stalePath = Path.Join(configDir, $"{profileName}.Arma3Profile");
                    if (File.Exists(stalePath))
                    {
                        try { File.Delete(stalePath); }
                        catch (IOException) { /* best effort; the server ignores this location anyway */ }
                    }
                }
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write config files for instance {Id} — server may be running", instance.Id);
        }

        // On Linux the -profiles= flag is broken; symlink the expected profile directory
        // into our managed config folder so Arma reads/writes the right profile.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            EnsureLinuxProfileSymlink(instance, configDir);
    }

    public string GetCommandLine(ServerInstance instance)
    {
        var executable = GetServerExecutable(instance);
        var args = BuildLaunchArguments(instance);
        return $"{executable} {args}";
    }

    private string BuildLaunchArguments(ServerInstance instance)
    {
        var configDir = GetInstanceConfigDirectory(instance);
        var profileName = $"server_{instance.Id}";

        var args = new List<string>
        {
            $"-port={instance.Port}",
            "-nosplash",
            "-world=empty",
            $"-name={profileName}"
        };

        // -profiles= is broken on Linux; the profile directory is handled via a symlink.
        // On Windows it works normally.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            args.Add($"\"-profiles={configDir}\"");

        if (instance.ServerCfgContent != null)
            args.Add($"\"-config={Path.Combine(configDir, "server.cfg")}\"");

        if (instance.BasicCfgContent != null)
            args.Add($"\"-cfg={Path.Combine(configDir, "basic.cfg")}\"");

        AddServerConfigArgs(instance, configDir, args);
        AddModArgs(instance, args);

        if (!string.IsNullOrEmpty(instance.AdditionalParameters))
            args.Add(instance.AdditionalParameters);

        return string.Join(" ", args);
    }

    private IServerConfigService GetServerConfigService()
        => serverConfigService ?? new ServerConfigService();

    private void AddServerConfigArgs(ServerInstance instance, string configDir, List<string> args)
    {
        if (instance.ServerCfgContent == null)
            return;

        var cfg = GetServerConfigService().ParseServerConfig(instance.ServerCfgContent);

        if (cfg.NetlogEnabled) args.Add("-netlog");
        if (cfg.AutoInit) args.Add("-autoInit");
        if (cfg.AllowedFilePatching > 0) args.Add("-filePatching");
        if (instance.EnableHT) args.Add("-enableHT");
        if (instance.EnableRanking)
            args.Add($"\"-ranking={Path.Combine(configDir, "ranking.log")}\"");
        if (instance.MaxMemOverride && instance.MaxMem > 0)
            args.Add($"-maxMem={instance.MaxMem}");
        if (instance.CpuCountOverride && instance.CpuCount > 0)
            args.Add($"-cpuCount={instance.CpuCount}");
    }

    private void AddModArgs(ServerInstance instance, List<string> args)
    {
        var dlcMods = GetDlcModsList(instance);
        var clientMods = GetClientModsList(instance);
        var allPlayerMods = dlcMods.Concat(clientMods).ToList();

        if (allPlayerMods.Count > 0)
            args.Add($"\"-mod={string.Join(";", allPlayerMods)}\"");

        var serverMods = GetServerModsList(instance);
        if (!string.IsNullOrEmpty(serverMods))
            args.Add($"\"-serverMod={serverMods}\"");
    }

    private static List<string> GetDlcModsList(ServerInstance instance)
    {
        var dlcMods = new List<string>();
        if (instance.ContactDlc) dlcMods.Add("contact");
        if (instance.GmDlc) dlcMods.Add("gm");
        if (instance.PfDlc) dlcMods.Add("vn");
        if (instance.CslaDlc) dlcMods.Add("csla");
        if (instance.WsDlc) dlcMods.Add("ws");
        if (instance.SpeDlc) dlcMods.Add("spe");
        if (instance.RfDlc) dlcMods.Add("rf");
        if (instance.EfDlc) dlcMods.Add("ef");
        return dlcMods;
    }

    private List<string> GetClientModsList(ServerInstance instance)
    {
        return instance.Mods
            .Where(m => m.IsClientSide)
            .OrderBy(m => m.LoadOrder)
            .Select(m => Path.Combine(GetInstanceModsDirectory(instance), $"@{SanitizeModName(m.SteamMod.Name)}"))
            .ToList();
    }

    private string GetServerModsList(ServerInstance instance)
    {
        var serverMods = instance.Mods
            .Where(m => m.IsServerSide)
            .OrderBy(m => m.LoadOrder)
            .Select(m => Path.Combine(GetInstanceModsDirectory(instance), $"@{SanitizeModName(m.SteamMod.Name)}"));

        return string.Join(";", serverMods);
    }

    private string BuildHeadlessClientArguments(ServerInstance instance, int hcIndex)
    {
        var configDir = GetInstanceConfigDirectory(instance);
        var args = new List<string>
        {
            "-client",
            $"-connect=127.0.0.1",
            $"-port={instance.Port}",
            "-nosound",
            "-world=empty",
            $"-name=server_{instance.Id}_hc{hcIndex}"
        };

        // A headless client is a regular client: it must join with the server
        // password and the same mod set or the server rejects it.
        if (instance.ServerCfgContent != null)
        {
            var cfg = GetServerConfigService().ParseServerConfig(instance.ServerCfgContent);
            if (!string.IsNullOrWhiteSpace(cfg.Password))
                args.Add($"-password={cfg.Password}");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            args.Add($"\"-profiles={configDir}\"");

        AddModArgs(instance, args);

        return string.Join(" ", args);
    }

    private static string SanitizeModName(string name)
    {
        var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        return string.IsNullOrEmpty(sanitized) ? "mod" : sanitized;
    }

    /// <summary>
    /// On Linux, Arma 3 ignores -profiles= and always writes profiles to
    /// ~/.local/share/Arma 3 - Other Profiles/&lt;name&gt;/. We create a symlink
    /// from that expected path to our managed KAST config directory so the
    /// profile file ends up where KAST expects it.
    /// </summary>
    private void EnsureLinuxProfileSymlink(ServerInstance instance, string configDir)
    {
        if (!TryGetLinuxProfilesDirectory(out var armaProfilesDir))
        {
            logger.LogWarning(
                "Cannot create Linux profile symlink for instance {Id}: the user running CASTER has no home directory. " +
                "Give the service user a writable home (or set HOME) so Arma 3 profile and difficulty settings are applied.",
                instance.Id);
            return;
        }

        var profileName = $"server_{instance.Id}";
        var symlinkPath = Path.Combine(armaProfilesDir, profileName);

        try
        {
            Directory.CreateDirectory(armaProfilesDir);

            if (Directory.Exists(symlinkPath) || File.Exists(symlinkPath))
            {
                if (Directory.ResolveLinkTarget(symlinkPath, false) != null)
                    File.Delete(symlinkPath); // remove stale symlink (unlink, not rmdir)
                else
                {
                    logger.LogWarning(
                        "Profile path for instance {Id} exists and is not a symlink; skipping symlink creation",
                        instance.Id);
                    return;
                }
            }

            var absoluteConfigDir = Path.GetFullPath(configDir);
            Directory.CreateSymbolicLink(symlinkPath, absoluteConfigDir);
            logger.LogInformation(
                "Created Linux profile symlink for instance {Id}", instance.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to create Linux profile symlink for instance {Id}", instance.Id);
        }
    }

    /// <summary>
    /// Resolves ~/.local/share/Arma 3 - Other Profiles. Fails when the process
    /// has no home directory: .NET then returns an empty folder path, and the
    /// combined path would silently become relative to the working directory.
    /// </summary>
    private static bool TryGetLinuxProfilesDirectory(out string armaProfilesDir)
    {
        var dataHome = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome))
        {
            armaProfilesDir = string.Empty;
            return false;
        }

        armaProfilesDir = Path.Combine(dataHome, "Arma 3 - Other Profiles");
        return true;
    }

    private void CleanupLinuxProfileSymlink(ServerInstance instance)
    {
        // Nothing could have been created without a home directory; never
        // delete a path relative to the working directory.
        if (!TryGetLinuxProfilesDirectory(out var armaProfilesDir))
            return;

        var profileName = $"server_{instance.Id}";
        var symlinkPath = Path.Combine(armaProfilesDir, profileName);

        try
        {
            // File.Delete uses unlink() which is the correct syscall for removing a symlink on Linux.
            // Directory.Delete uses rmdir() which always fails on symlinks.
            if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
                logger.LogInformation("Removed Linux profile symlink");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to remove Linux profile symlink for instance {Id}", instance.Id);
        }
    }

    private static async Task CloseProcessHistoryEntryAsync(KastDbContext db, int instanceId, int processId, string reason, int? exitCode = null, CancellationToken ct = default)
    {
        var history = await db.ServerInstanceProcessHistories
            .Where(h => h.ServerInstanceId == instanceId && h.ProcessId == processId && h.EndedAt == null)
            .OrderByDescending(h => h.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (history is not null)
        {
            history.EndedAt = DateTime.UtcNow;
            history.TerminationReason = reason;
            history.ExitCode = exitCode;
        }
    }
}
