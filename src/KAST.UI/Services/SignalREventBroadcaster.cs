using KAST.Core.Events;
using KAST.Core.Interfaces;
using KAST.UI.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace KAST.UI.Services;

/// <summary>
/// Bridges domain events from Infrastructure services to SignalR hubs.
/// </summary>
public class SignalREventBroadcaster(
    IHubContext<DownloadHub> downloadHub,
    IHubContext<MonitoringHub> monitoringHub,
    ServerConsoleStore consoleStore,
    ServerRuntimeEventStore runtimeEventStore,
    IOutputSanitizer sanitizer,
    MonitoringSnapshotService snapshots) : IAppEventBroadcaster
{
    public event Action<ModDownloadProgressEvent>? OnModDownloadProgress;
    public event Action<ModStatusChangedEvent>? OnModStatusChanged;
    public event Action<ServerStatusChangedEvent>? OnServerStatusChanged;

    public Task BroadcastDownloadProgressAsync(ModDownloadProgressEvent progress)
    {
        OnModDownloadProgress?.Invoke(progress);
        return DownloadHub.BroadcastDownloadProgress(downloadHub, progress);
    }

    public Task BroadcastModStatusChangedAsync(ModStatusChangedEvent status)
    {
        OnModStatusChanged?.Invoke(status);
        return DownloadHub.BroadcastModStatusChanged(downloadHub, status);
    }

    public Task BroadcastServerStatusChangedAsync(ServerStatusChangedEvent status)
    {
        OnServerStatusChanged?.Invoke(status);
        return MonitoringHub.BroadcastServerStatus(monitoringHub, status);
    }

    public Task BroadcastHostMetricsAsync(HostMetricsUpdatedEvent metrics)
        => MonitoringHub.BroadcastHostMetrics(monitoringHub, metrics);

    public Task BroadcastInstanceMetricsAsync(InstanceMetricsUpdatedEvent metrics)
        => MonitoringHub.BroadcastInstanceMetrics(monitoringHub, metrics);

    public Task BroadcastLogEntryAsync(LogEntryEvent logEntry)
    {
        var safeLogEntry = logEntry with { Line = sanitizer.Sanitize(logEntry.Line) };
        consoleStore.Add(safeLogEntry);
        return MonitoringHub.BroadcastLogEntry(monitoringHub, safeLogEntry);
    }

    public Task BroadcastServerRuntimeEventAsync(ServerRuntimeEvent runtimeEvent)
    {
        // Every field rendered in the UI originates from game-client-controlled
        // RPT text — sanitize all of them, not just a subset.
        var safeRuntimeEvent = runtimeEvent with
        {
            Title = sanitizer.Sanitize(runtimeEvent.Title),
            Message = sanitizer.Sanitize(runtimeEvent.Message),
            SourceLine = runtimeEvent.SourceLine is null ? null : sanitizer.Sanitize(runtimeEvent.SourceLine),
            MissionFile = runtimeEvent.MissionFile is null ? null : sanitizer.Sanitize(runtimeEvent.MissionFile),
            MissionWorld = runtimeEvent.MissionWorld is null ? null : sanitizer.Sanitize(runtimeEvent.MissionWorld),
            MissionDirectory = runtimeEvent.MissionDirectory is null ? null : sanitizer.Sanitize(runtimeEvent.MissionDirectory),
            PlayerName = runtimeEvent.PlayerName is null ? null : sanitizer.Sanitize(runtimeEvent.PlayerName),
            PlayerUid = runtimeEvent.PlayerUid is null ? null : sanitizer.Sanitize(runtimeEvent.PlayerUid),
            PlayerIp = runtimeEvent.PlayerIp is null ? null : sanitizer.Sanitize(runtimeEvent.PlayerIp)
        };
        runtimeEventStore.Add(safeRuntimeEvent);
        return MonitoringHub.BroadcastServerRuntimeEvent(monitoringHub, safeRuntimeEvent);
    }

    public Task BroadcastExternalProcessesAsync(ExternalProcessesUpdatedEvent processes)
    {
        snapshots.UpdateExternalProcesses(processes.Processes);
        return MonitoringHub.BroadcastExternalProcesses(monitoringHub, processes);
    }
}
