using KAST.Core.Enums;
using KAST.Core.Events;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using KAST.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KAST.UI.Services;

/// <summary>
/// Keeps the database honest about the processes on the host. Every sweep:
/// <list type="number">
/// <item>reaps orphaned Arma processes (adopts a lost server, kills strays),</item>
/// <item>marks Running instances whose process died as Crashed,</item>
/// <item>resolves instances stranded in Starting/Stopping/Restarting,</item>
/// <item>applies the RestartPolicy to crashes — including those recorded by
/// the process exit handler, which used to win the race and leave the
/// watchdog with nothing to restart.</item>
/// </list>
/// Every change to an instance is made under its lifecycle lock, and an
/// instance with an operation in flight is skipped until the next sweep.
/// </summary>
public class ProcessWatchdogService(
    IServiceScopeFactory scopeFactory,
    IProcessManagerService processManager,
    IAppEventBroadcaster broadcaster,
    IServerConsoleLogTailer consoleLogTailer,
    ILogger<ProcessWatchdogService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>A server that stays up this long has its crash budget reset.</summary>
    internal static TimeSpan StableRunReset { get; set; } = TimeSpan.FromMinutes(5);

    private readonly Dictionary<int, int> _restartAttempts = [];
    private readonly Dictionary<int, int> _autoStartedPids = [];
    private readonly HashSet<int> _pendingRetry = [];
    private DateTime _lastCrashScanUtc = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Process watchdog service started");

        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            { await CheckRunningInstancesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception ex)
            { logger.LogWarning(ex, "Error in process watchdog check"); }
        }

        logger.LogInformation("Process watchdog service stopped");
    }

    internal async Task CheckRunningInstancesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KastDbContext>();
        var serverService = scope.ServiceProvider.GetRequiredService<IServerInstanceService>();

        // ── 0. Process hunting ──
        try
        {
            await serverService.ReapOrphanedProcessesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Orphaned process sweep failed");
        }

        var crashed = new HashSet<int>();

        // ── 1. Running instances with dead processes ──
        var runningIds = await db.ServerInstances
            .AsNoTracking()
            .Where(s => s.Status == ServerInstanceStatus.Running && s.ProcessId != null)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in runningIds)
        {
            using var gate = await InstanceLifecycleLocks.TryEnterAsync(id, TimeSpan.Zero, ct);
            if (gate is null)
                continue; // operation in flight

            var instance = await LoadFreshAsync(db, id, ct);
            if (instance is not { Status: ServerInstanceStatus.Running, ProcessId: { } pid })
                continue;

            if (processManager.IsProcessRunning(pid))
            {
                ResetBudgetIfDeserved(instance, pid);
                // Re-attach the RPT tailer (idempotent) — e.g. after an adoption.
                consoleLogTailer.StartFollowing(instance, instance.StartedAt ?? DateTime.UtcNow, replayExistingContent: false);
                continue;
            }

            logger.LogWarning("Server {Name} (PID {Pid}) has crashed", instance.Name, pid);
            await consoleLogTailer.StopFollowingAsync(instance.Id);

            CloseHistoryEntry(db, instance.Id, pid, "Crashed");
            instance.ProcessId = null;
            instance.Status = ServerInstanceStatus.Crashed;
            instance.StartedAt = null;
            await KillHeadlessClientsAsync(instance);
            await db.SaveChangesAsync(ct);
            await broadcaster.BroadcastServerStatusChangedAsync(
                new ServerStatusChangedEvent(instance.Id, instance.Status));

            crashed.Add(instance.Id);
        }

        // ── 2. Instances stranded in a transitional state ──
        var transitionalIds = await db.ServerInstances
            .AsNoTracking()
            .Where(s => s.Status == ServerInstanceStatus.Starting
                     || s.Status == ServerInstanceStatus.Stopping
                     || s.Status == ServerInstanceStatus.Restarting)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in transitionalIds)
        {
            using var gate = await InstanceLifecycleLocks.TryEnterAsync(id, TimeSpan.Zero, ct);
            if (gate is null)
                continue; // the operation that owns the state is still running

            var instance = await LoadFreshAsync(db, id, ct);
            if (instance is null || instance.Status is not (ServerInstanceStatus.Starting
                    or ServerInstanceStatus.Stopping or ServerInstanceStatus.Restarting))
                continue;

            var alive = instance.ProcessId is { } pid && processManager.IsProcessRunning(pid);
            var previous = instance.Status;
            if (alive)
            {
                instance.Status = ServerInstanceStatus.Running;
            }
            else
            {
                if (instance.ProcessId is { } deadPid)
                    CloseHistoryEntry(db, instance.Id, deadPid, "Lost");
                instance.ProcessId = null;
                instance.StartedAt = null;
                instance.Status = previous == ServerInstanceStatus.Restarting
                    ? ServerInstanceStatus.Crashed
                    : ServerInstanceStatus.Stopped;
            }

            logger.LogWarning("Server {Name} was stuck in {Previous}; resolved to {Status}",
                instance.Name, previous, instance.Status);
            await db.SaveChangesAsync(ct);
            await broadcaster.BroadcastServerStatusChangedAsync(
                new ServerStatusChangedEvent(instance.Id, instance.Status));
        }

        // ── 3. Orphaned headless client rows ──
        var orphanedHCs = await db.HeadlessClients
            .Where(h => h.ProcessId != null)
            .ToListAsync(ct);

        var hcChanged = false;
        foreach (var hc in orphanedHCs.Where(h => !InstanceLifecycleLocks.IsBusy(h.ServerInstanceId)
                                               && !processManager.IsProcessRunning(h.ProcessId!.Value)))
        {
            logger.LogWarning("Headless client {Id} (PID {Pid}) is gone; clearing", hc.Id, hc.ProcessId);
            hc.ProcessId = null;
            hc.Status = ServerInstanceStatus.Stopped;
            hc.StartedAt = null;
            hcChanged = true;
        }

        if (hcChanged)
            await db.SaveChangesAsync(ct);

        // ── 4. Crashes recorded by the exit handler since the last sweep ──
        var scanEnd = DateTime.UtcNow;
        var scanStart = _lastCrashScanUtc;
        _lastCrashScanUtc = scanEnd;

        var exits = await db.ServerInstanceProcessHistories
            .AsNoTracking()
            .Where(h => h.EndedAt > scanStart && h.EndedAt <= scanEnd
                     && (h.TerminationReason == "Crashed" || h.TerminationReason == "Exited"))
            .Select(h => new { h.ServerInstanceId, h.TerminationReason })
            .ToListAsync(ct);

        var cleanExits = new HashSet<int>();
        foreach (var exit in exits)
        {
            if (exit.TerminationReason == "Crashed")
                crashed.Add(exit.ServerInstanceId);
            else
                cleanExits.Add(exit.ServerInstanceId);
        }

        // ── 5. Apply restart policies ──
        var candidates = crashed.Concat(cleanExits).Concat(_pendingRetry).Distinct().ToList();
        foreach (var id in candidates)
        {
            var instance = await LoadFreshAsync(db, id, ct);
            if (instance is null)
            {
                Forget(id);
                continue;
            }

            if (instance.ProcessId is not null
                || instance.Status is not (ServerInstanceStatus.Crashed or ServerInstanceStatus.Stopped))
            {
                _pendingRetry.Remove(id);
                continue; // someone already brought it back (or is doing so)
            }

            var wantsRestart = instance.RestartPolicy switch
            {
                RestartPolicy.OnCrash => crashed.Contains(id) || (_pendingRetry.Contains(id) && instance.Status == ServerInstanceStatus.Crashed),
                RestartPolicy.Always => true,
                _ => false
            };

            if (!wantsRestart)
            {
                _pendingRetry.Remove(id);
                continue;
            }

            await TryRestartAsync(instance, serverService, ct);
        }
    }

    private static async Task<ServerInstance?> LoadFreshAsync(KastDbContext db, int id, CancellationToken ct)
    {
        var instance = await db.ServerInstances
            .Include(s => s.HeadlessClients)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (instance is not null)
            await db.Entry(instance).ReloadAsync(ct); // the scope may already track a stale copy
        return instance;
    }

    private void ResetBudgetIfDeserved(ServerInstance instance, int pid)
    {
        if (!_restartAttempts.ContainsKey(instance.Id))
            return;

        // A start the watchdog did not make (manual, schedule) or a run that
        // stayed up long enough proves the crash loop is over.
        var manualStart = !_autoStartedPids.TryGetValue(instance.Id, out var autoPid) || autoPid != pid;
        var stable = instance.StartedAt is { } started && DateTime.UtcNow - started >= StableRunReset;
        if (!manualStart && !stable)
            return;

        _restartAttempts.Remove(instance.Id);
        _autoStartedPids.Remove(instance.Id);
        logger.LogInformation("Server {Name}: cleared restart attempt counter ({Reason})",
            instance.Name, manualStart ? "started outside the watchdog" : "stable run");
    }

    private async Task TryRestartAsync(ServerInstance instance, IServerInstanceService serverService, CancellationToken ct)
    {
        _restartAttempts.TryGetValue(instance.Id, out var attempts);

        if (attempts >= instance.MaxRestartAttempts)
        {
            logger.LogError(
                "Server {Name}: exceeded max restart attempts ({Max}), leaving {Status}",
                instance.Name, instance.MaxRestartAttempts, instance.Status);
            _pendingRetry.Remove(instance.Id);
            return;
        }

        _restartAttempts[instance.Id] = attempts + 1;

        logger.LogInformation(
            "Server {Name}: restarting (attempt {Attempt}/{Max}, policy={Policy})",
            instance.Name, attempts + 1, instance.MaxRestartAttempts, instance.RestartPolicy);

        try
        {
            await serverService.StartInstanceAsync(instance.Id, ct);
            _pendingRetry.Remove(instance.Id);

            var started = await serverService.GetInstanceByIdAsync(instance.Id, ct);
            if (started?.ProcessId is { } newPid)
                _autoStartedPids[instance.Id] = newPid;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _pendingRetry.Add(instance.Id);
            logger.LogError(ex, "Server {Name}: restart attempt {Attempt} failed", instance.Name, attempts + 1);
        }
    }

    private async Task KillHeadlessClientsAsync(ServerInstance instance)
    {
        foreach (var hc in instance.HeadlessClients.Where(h => h.ProcessId.HasValue))
        {
            try { await processManager.KillProcessAsync(hc.ProcessId!.Value, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to kill headless client {Pid} of crashed server {Name}", hc.ProcessId, instance.Name); }
            hc.ProcessId = null;
            hc.Status = ServerInstanceStatus.Stopped;
            hc.StartedAt = null;
        }
    }

    private void Forget(int id)
    {
        _restartAttempts.Remove(id);
        _autoStartedPids.Remove(id);
        _pendingRetry.Remove(id);
    }

    private static void CloseHistoryEntry(KastDbContext db, int instanceId, int processId, string reason)
    {
        var history = db.ServerInstanceProcessHistories
            .Where(h => h.ServerInstanceId == instanceId && h.ProcessId == processId && h.EndedAt == null)
            .OrderByDescending(h => h.StartedAt)
            .FirstOrDefault();
        if (history is not null)
        {
            history.EndedAt = DateTime.UtcNow;
            history.TerminationReason = reason;
        }
    }
}
