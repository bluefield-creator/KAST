using KAST.Core.Enums;
using KAST.Core.Models;
using MudBlazor;

namespace KAST.UI.Helpers;

/// <summary>
/// The one status vocabulary of the UI: every badge, dot, icon and label for
/// a server or mod state comes from here.
/// </summary>
public static class UiHelpers
{
    public static string GetStatusLabel(ServerInstanceStatus status) => status switch
    {
        ServerInstanceStatus.Running => "Running",
        ServerInstanceStatus.Starting => "Starting",
        ServerInstanceStatus.Stopping => "Stopping",
        ServerInstanceStatus.Restarting => "Restarting",
        ServerInstanceStatus.Downloading => "Installing",
        ServerInstanceStatus.Crashed => "Crashed",
        _ => "Stopped"
    };

    public static Color GetStatusColor(ServerInstanceStatus status) => status switch
    {
        ServerInstanceStatus.Running => Color.Success,
        ServerInstanceStatus.Crashed => Color.Error,
        ServerInstanceStatus.Starting or ServerInstanceStatus.Stopping
            or ServerInstanceStatus.Restarting or ServerInstanceStatus.Downloading => Color.Warning,
        _ => Color.Default
    };

    /// <summary>CSS class for a <c>.k-dot</c> status dot.</summary>
    public static string GetStatusDotClass(ServerInstanceStatus status) => status switch
    {
        ServerInstanceStatus.Running => "k-dot-ok",
        ServerInstanceStatus.Crashed => "k-dot-err",
        ServerInstanceStatus.Starting or ServerInstanceStatus.Stopping
            or ServerInstanceStatus.Restarting or ServerInstanceStatus.Downloading => "k-dot-warn",
        _ => ""
    };

    /// <summary>True while a lifecycle operation is in flight (no start/stop allowed).</summary>
    public static bool IsTransitional(ServerInstanceStatus status) => status is
        ServerInstanceStatus.Starting or ServerInstanceStatus.Stopping
        or ServerInstanceStatus.Restarting or ServerInstanceStatus.Downloading;

    public static string GetStatusIcon(ServerInstanceStatus status) => status switch
    {
        ServerInstanceStatus.Running => Icons.Material.Outlined.PlayCircle,
        ServerInstanceStatus.Crashed => Icons.Material.Outlined.ErrorOutline,
        ServerInstanceStatus.Starting or ServerInstanceStatus.Restarting => Icons.Material.Outlined.HourglassTop,
        ServerInstanceStatus.Stopping => Icons.Material.Outlined.HourglassBottom,
        ServerInstanceStatus.Downloading => Icons.Material.Outlined.Downloading,
        _ => Icons.Material.Outlined.StopCircle
    };

    public static string GetModStatusLabel(ModStatus status) => status switch
    {
        ModStatus.Installed => "Installed",
        ModStatus.Downloading => "Downloading",
        ModStatus.Updating => "Updating",
        ModStatus.UpdateAvailable => "Update available",
        ModStatus.Error => "Error",
        _ => "Not installed"
    };

    public static string FormatUptime(DateTime? startedUtc)
    {
        if (startedUtc is null) return "—";
        var span = DateTime.UtcNow - startedUtc.Value;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    public static Color GetModStatusColor(ModStatus status) => status switch
    {
        ModStatus.Installed => Color.Success,
        ModStatus.Downloading or ModStatus.Updating => Color.Warning,
        ModStatus.Error => Color.Error,
        ModStatus.UpdateAvailable => Color.Warning,
        _ => Color.Default
    };

    public static string GetModStatusIcon(ModStatus status) => status switch
    {
        ModStatus.Installed => Icons.Material.Outlined.CheckCircle,
        ModStatus.Downloading => Icons.Material.Outlined.CloudDownload,
        ModStatus.Updating => Icons.Material.Outlined.Sync,
        ModStatus.Error => Icons.Material.Outlined.ErrorOutline,
        ModStatus.UpdateAvailable => Icons.Material.Outlined.NewReleases,
        _ => Icons.Material.Outlined.RadioButtonUnchecked
    };

    public static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0; double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return $"{size:F1} {sizes[order]}";
    }

    public static string FormatRelativeTime(DateTime utc)
    {
        var elapsed = DateTime.UtcNow - utc;
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays}d ago";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)}mo ago";
        return $"{(int)(elapsed.TotalDays / 365)}y ago";
    }
}
