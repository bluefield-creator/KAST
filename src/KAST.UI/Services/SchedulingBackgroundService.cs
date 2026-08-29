using KAST.Core.Enums;
using KAST.Core.Interfaces;
using KAST.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KAST.UI.Services;

/// <summary>
/// Evaluates server instance schedules every minute and starts/stops
/// instances based on their AutoStartTime and AutoStopTime (HH:mm format).
/// A schedule fires when its time-of-day falls inside the window since the
/// previous evaluation, so timer drift, GC pauses, or a slow query can never
/// skip a scheduled action — exact-equality matching (the old behavior)
/// required the tick to land on the precise instant and effectively never fired.
/// Times are interpreted in the host's local time zone.
/// </summary>
public class SchedulingBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SchedulingBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private DateTime _lastEvaluation = DateTime.Now;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduling background service started");

        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            { await EvaluateSchedulesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception ex)
            { logger.LogWarning(ex, "Error evaluating schedules"); }
        }

        logger.LogInformation("Scheduling background service stopped");
    }

    /// <summary>
    /// True when <paramref name="scheduled"/> lies in the half-open window
    /// (<paramref name="windowStart"/>, <paramref name="windowEnd"/>], handling
    /// the midnight wrap-around.
    /// </summary>
    internal static bool ShouldFire(TimeOnly scheduled, TimeOnly windowStart, TimeOnly windowEnd)
    {
        if (windowStart == windowEnd)
            return false;

        return windowStart < windowEnd
            ? scheduled > windowStart && scheduled <= windowEnd
            : scheduled > windowStart || scheduled <= windowEnd; // window spans midnight
    }

    private async Task EvaluateSchedulesAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var windowStart = TimeOnly.FromDateTime(_lastEvaluation);
        var windowEnd = TimeOnly.FromDateTime(now);
        _lastEvaluation = now;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KastDbContext>();
        var serverService = scope.ServiceProvider.GetRequiredService<IServerInstanceService>();

        var scheduledInstances = await db.ServerInstances
            .Where(s => s.ScheduleEnabled)
            .ToListAsync(ct);

        foreach (var instance in scheduledInstances)
        {
            if (instance.AutoStartTime is { } startTime
                && ShouldFire(startTime, windowStart, windowEnd)
                && instance.Status == ServerInstanceStatus.Stopped)
            {
                logger.LogInformation(
                    "Schedule: starting server {Name} (scheduled at {Time})",
                    instance.Name, instance.AutoStartTime);
                try
                { await serverService.StartInstanceAsync(instance.Id, ct); }
                catch (Exception ex)
                { logger.LogError(ex, "Schedule: failed to start server {Name}", instance.Name); }
            }

            if (instance.AutoStopTime is not { } stopTime
                || !ShouldFire(stopTime, windowStart, windowEnd)
                || instance.Status != ServerInstanceStatus.Running) continue;

            logger.LogInformation(
                "Schedule: stopping server {Name} (scheduled at {Time})",
                instance.Name, instance.AutoStopTime);
            try { await serverService.StopInstanceAsync(instance.Id, ct); }
            catch (Exception ex)
            { logger.LogError(ex, "Schedule: failed to stop server {Name}", instance.Name); }
        }
    }
}
