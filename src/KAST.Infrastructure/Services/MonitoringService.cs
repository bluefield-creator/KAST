using System.Diagnostics;
using System.Runtime.InteropServices;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KAST.Infrastructure.Services;

public class MonitoringService(
    IProcessManagerService processManager,
    Data.KastDbContext db,
    IServerQueryService? serverQuery = null) : IMonitoringService
{
    public async Task<HostMetrics> GetHostMetricsAsync(CancellationToken ct = default)
    {
        var metrics = new HostMetrics();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await ReadLinuxCpuAsync(metrics, ct);
            await ReadLinuxMemoryAsync(metrics, ct);
            ReadLinuxDisk(metrics);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await ReadWindowsMetricsAsync(metrics, ct);
        }

        return metrics;
    }

    public async Task<InstanceMetrics?> GetInstanceMetricsAsync(int serverInstanceId, CancellationToken ct = default)
    {
        var instance = await db.ServerInstances.FindAsync([serverInstanceId], ct);
        if (instance?.ProcessId == null)
            return null;

        return await SampleInstanceAsync(serverInstanceId, instance.ProcessId.Value, instance.SteamQueryPort, ct);
    }

    public async Task<IReadOnlyList<InstanceMetrics>> GetAllInstanceMetricsAsync(CancellationToken ct = default)
    {
        var instances = await db.ServerInstances
            .AsNoTracking()
            .Where(s => s.ProcessId != null)
            .Select(s => new { s.Id, ProcessId = s.ProcessId!.Value, s.SteamQueryPort })
            .ToListAsync(ct);

        var samples = await Task.WhenAll(instances.Select(
            instance => SampleInstanceAsync(instance.Id, instance.ProcessId, instance.SteamQueryPort, ct)));

        return samples.Where(m => m is not null).Select(m => m!).ToList();
    }

    private async Task<InstanceMetrics?> SampleInstanceAsync(int instanceId, int processId, int steamQueryPort, CancellationToken ct)
    {
        // Process metrics (500 ms CPU delta) and the A2S player query run
        // concurrently so an unresponsive query port never slows the sample.
        var processTask = processManager.GetProcessMetricsAsync(processId, ct);
        var queryTask = serverQuery is not null
            ? serverQuery.QueryAsync("127.0.0.1", steamQueryPort, ct)
            : Task.FromResult<ServerQueryResult?>(null);

        var processMetrics = await processTask;
        var query = await queryTask;

        if (processMetrics == null)
            return null;

        return new InstanceMetrics
        {
            ServerInstanceId = instanceId,
            CpuUsagePercent = processMetrics.Value.CpuPercent,
            MemoryUsageBytes = processMetrics.Value.MemoryBytes,
            PlayerCount = query?.PlayerCount ?? 0,
            MaxPlayers = query?.MaxPlayers ?? 0
        };
    }

    private static async Task ReadLinuxCpuAsync(HostMetrics metrics, CancellationToken ct)
    {
        var stat1 = await File.ReadAllTextAsync("/proc/stat", ct);
        await Task.Delay(500, ct);
        var stat2 = await File.ReadAllTextAsync("/proc/stat", ct);

        var v1 = ParseCpuLine(stat1);
        var v2 = ParseCpuLine(stat2);
        if (v1.Length < 5 || v2.Length < 5)
            return; // malformed /proc/stat — leave CPU at 0 rather than throwing

        var idle1 = v1[3] + v1[4];
        var idle2 = v2[3] + v2[4];
        var total1 = v1.Sum();
        var total2 = v2.Sum();

        var totalDiff = total2 - total1;
        var idleDiff = idle2 - idle1;

        metrics.CpuUsagePercent = totalDiff == 0 ? 0 : (1.0 - (double)idleDiff / totalDiff) * 100;
    }

    internal static long[] ParseCpuLine(string statContent)
    {
        var line = statContent.Split('\n')[0]; // "cpu  ..."
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(v => long.TryParse(v, out var parsed) ? parsed : 0)
            .ToArray();
    }

    private static async Task ReadLinuxMemoryAsync(HostMetrics metrics, CancellationToken ct)
    {
        var memInfo = await File.ReadAllLinesAsync("/proc/meminfo", ct);
        long total = 0, available = 0;

        foreach (var line in memInfo)
        {
            if (line.StartsWith("MemTotal:"))
                total = ParseKb(line);
            else if (line.StartsWith("MemAvailable:"))
                available = ParseKb(line);
        }

        metrics.TotalMemoryBytes = total * 1024;
        metrics.UsedMemoryBytes = (total - available) * 1024;
        metrics.MemoryUsagePercent = total == 0 ? 0 : (double)(total - available) / total * 100;

        static long ParseKb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
        }
    }

    private static void ReadLinuxDisk(HostMetrics metrics)
    {
        var drive = new DriveInfo("/");
        metrics.TotalDiskBytes = drive.TotalSize;
        metrics.UsedDiskBytes = drive.TotalSize - drive.AvailableFreeSpace;
        metrics.DiskUsagePercent = drive.TotalSize == 0 ? 0 : (double)metrics.UsedDiskBytes / drive.TotalSize * 100;
    }

    private static async Task ReadWindowsMetricsAsync(HostMetrics metrics, CancellationToken ct)
    {
        // Host CPU via GetSystemTimes delta — the previous implementation measured
        // KAST's own process time and reported it as host CPU.
        if (WindowsHostApi.TryGetSystemTimes(out var idle1, out var kernel1, out var user1))
        {
            await Task.Delay(500, ct);
            if (WindowsHostApi.TryGetSystemTimes(out var idle2, out var kernel2, out var user2))
            {
                var idleDiff = idle2 - idle1;
                var totalDiff = (kernel2 - kernel1) + (user2 - user1); // kernel includes idle
                metrics.CpuUsagePercent = totalDiff == 0 ? 0 : (1.0 - (double)idleDiff / totalDiff) * 100;
            }
        }

        // Host memory via GlobalMemoryStatusEx — GC.GetGCMemoryInfo only knows
        // about this process's view of memory.
        if (WindowsHostApi.TryGetMemoryStatus(out var totalBytes, out var availableBytes))
        {
            metrics.TotalMemoryBytes = (long)totalBytes;
            metrics.UsedMemoryBytes = (long)(totalBytes - availableBytes);
            metrics.MemoryUsagePercent = totalBytes == 0 ? 0 : (double)metrics.UsedMemoryBytes / metrics.TotalMemoryBytes * 100;
        }

        // Disk
        var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\");
        metrics.TotalDiskBytes = drive.TotalSize;
        metrics.UsedDiskBytes = drive.TotalSize - drive.AvailableFreeSpace;
        metrics.DiskUsagePercent = drive.TotalSize == 0 ? 0 : (double)metrics.UsedDiskBytes / drive.TotalSize * 100;
    }

    private static class WindowsHostApi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        public static bool TryGetMemoryStatus(out ulong totalBytes, out ulong availableBytes)
        {
            totalBytes = 0;
            availableBytes = 0;

            if (!OperatingSystem.IsWindows())
                return false;

            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                return false;

            totalBytes = status.TotalPhys;
            availableBytes = status.AvailPhys;
            return true;
        }

        public static bool TryGetSystemTimes(out long idle, out long kernel, out long user)
        {
            idle = kernel = user = 0;
            return OperatingSystem.IsWindows() && GetSystemTimes(out idle, out kernel, out user);
        }
    }
}
