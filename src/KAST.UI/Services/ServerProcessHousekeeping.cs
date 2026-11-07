using KAST.Core.Interfaces;
using KAST.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ServerInstanceStatus = KAST.Core.Enums.ServerInstanceStatus;

namespace KAST.UI.Services;

/// <summary>
/// Panel start/stop housekeeping for Arma processes: nothing the panel
/// launched may outlive it, and nothing a previous run left behind may block
/// the next start.
/// </summary>
internal static class ServerProcessHousekeeping
{
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Force-kills every process the database still associates with an
    /// instance or headless client — in any state, not only Running — in
    /// parallel and within <see cref="ShutdownBudget"/>.
    /// </summary>
    public static void KillAllOnShutdown(IServiceProvider services, ILogger logger)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KastDbContext>();
            var processManager = scope.ServiceProvider.GetRequiredService<IProcessManagerService>();

            var instances = db.ServerInstances
                .Include(s => s.HeadlessClients)
                .Where(s => s.ProcessId != null || s.HeadlessClients.Any(h => h.ProcessId != null))
                .ToList();
            if (instances.Count == 0)
                return;

            var kills = new List<(Func<bool> Apply, Task<bool> Task, int Pid)>();
            foreach (var instance in instances)
            {
                foreach (var hc in instance.HeadlessClients.Where(h => h.ProcessId.HasValue))
                {
                    var hcRow = hc;
                    var pid = hc.ProcessId!.Value;
                    kills.Add((() =>
                    {
                        hcRow.ProcessId = null;
                        hcRow.Status = ServerInstanceStatus.Stopped;
                        hcRow.StartedAt = null;
                        return true;
                    }, SafeKill(processManager, pid, logger), pid));
                }

                if (instance.ProcessId is { } serverPid)
                {
                    var row = instance;
                    kills.Add((() =>
                    {
                        CloseHistory(db, row.Id, serverPid, "Shutdown");
                        row.ProcessId = null;
                        row.Status = ServerInstanceStatus.Stopped;
                        row.StartedAt = null;
                        return true;
                    }, SafeKill(processManager, serverPid, logger), serverPid));
                }
            }

            Task.WaitAll(kills.Select(k => (Task)k.Task).ToArray(), ShutdownBudget);

            foreach (var kill in kills)
            {
                if (kill.Task.IsCompletedSuccessfully && kill.Task.Result)
                    kill.Apply();
                else
                    logger.LogWarning("PID {Pid} could not be killed during shutdown; it will be reaped on next start", kill.Pid);
            }

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during shutdown process cleanup");
        }
    }

    /// <summary>
    /// Resets every instance and headless client row left in a non-final
    /// state by a previous run, kills processes it still references, then
    /// reaps any tagged Arma process nobody references any more.
    /// </summary>
    public static async Task ResetOnStartupAsync(IServiceProvider services, ILogger logger)
    {
        var db = services.GetRequiredService<KastDbContext>();
        var processManager = services.GetRequiredService<IProcessManagerService>();

        var stuckInstances = await db.ServerInstances
            .Where(s => s.ProcessId != null
                     || s.Status == ServerInstanceStatus.Running
                     || s.Status == ServerInstanceStatus.Starting
                     || s.Status == ServerInstanceStatus.Stopping
                     || s.Status == ServerInstanceStatus.Restarting
                     || s.Status == ServerInstanceStatus.Downloading)
            .ToListAsync();

        foreach (var instance in stuckInstances)
        {
            if (instance.ProcessId is { } pid)
            {
                await SafeKill(processManager, pid, logger);
                CloseHistory(db, instance.Id, pid, "Orphaned");
            }

            if (instance.Status != ServerInstanceStatus.Stopped)
                logger.LogWarning("Instance {Name} was left {Status} by the previous run; reset to Stopped",
                    instance.Name, instance.Status);

            instance.ProcessId = null;
            instance.Status = ServerInstanceStatus.Stopped;
            instance.StartedAt = null;
        }

        var stuckHCs = await db.HeadlessClients
            .Where(h => h.ProcessId != null || h.Status != ServerInstanceStatus.Stopped)
            .ToListAsync();
        foreach (var hc in stuckHCs)
        {
            if (hc.ProcessId is { } pid)
                await SafeKill(processManager, pid, logger);

            hc.ProcessId = null;
            hc.Status = ServerInstanceStatus.Stopped;
            hc.StartedAt = null;
        }

        if (stuckInstances.Count > 0 || stuckHCs.Count > 0)
            await db.SaveChangesAsync();

        // Processes whose PID never made it into the database (or was
        // overwritten) are only findable by their instance tag.
        try
        {
            var reaped = await services.GetRequiredService<IServerInstanceService>().ReapOrphanedProcessesAsync();
            if (reaped > 0)
                logger.LogWarning("Reaped {Count} orphaned Arma process(es) left by the previous run", reaped);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup orphan sweep failed");
        }
    }

    private static async Task<bool> SafeKill(IProcessManagerService processManager, int pid, ILogger logger)
    {
        try
        {
            return await processManager.KillProcessAsync(pid, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to kill PID {Pid}", pid);
            return false;
        }
    }

    private static void CloseHistory(KastDbContext db, int instanceId, int pid, string reason)
    {
        var history = db.ServerInstanceProcessHistories
            .Where(h => h.ServerInstanceId == instanceId && h.ProcessId == pid && h.EndedAt == null)
            .OrderByDescending(h => h.StartedAt)
            .FirstOrDefault();
        if (history is not null)
        {
            history.EndedAt = DateTime.UtcNow;
            history.TerminationReason = reason;
        }
    }
}
