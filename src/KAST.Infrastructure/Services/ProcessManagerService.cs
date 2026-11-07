using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using KAST.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KAST.Infrastructure.Services;

public partial class ProcessManagerService(ILogger<ProcessManagerService> logger, IServiceScopeFactory scopeFactory) : IProcessManagerService
{
    /// <summary>How long a process gets to exit after a delivered SIGTERM / close request.</summary>
    internal static TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on waiting for a killed process to disappear. A process stuck
    /// in uninterruptible I/O or held by a crash handler must never hang a stop
    /// (and with it the panel's Stopping state) forever.
    /// </summary>
    internal static TimeSpan KillWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public async Task<int> StartServerProcessAsync(string executablePath, string arguments,
        Action<int, string>? onOutputLine = null, Action<int, int>? onProcessExited = null,
        CancellationToken ct = default)
    {
        using var activity = KastActivitySources.Process.StartActivity(
            "kast.process.start", ActivityKind.Internal);

        logger.LogInformation("Starting server process");

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Nothing ever writes to the server's stdin; never let it inherit ours.
            RedirectStandardInput = false
        };

        ct.ThrowIfCancellationRequested();

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start server process.");

        var pid = process.Id;
        logger.LogInformation("Process started with PID {Pid}", pid);
        activity?.SetTag("process.pid", pid);

        // Always drain stdout/stderr: an unread redirected pipe fills up and
        // blocks the server's next write, freezing it until someone kills it.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) SafeInvoke(onOutputLine, pid, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) SafeInvoke(onOutputLine, pid, e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Exit watcher. Uses the Exited event instead of WaitForExitAsync:
        // WaitForExitAsync also waits for EOF on the redirected pipes, which
        // never arrives while any grandchild still holds an inherited handle —
        // the exit callback then never fires and the panel shows a dead server
        // as Running.
        var exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitSignal.TrySetResult();
        if (process.HasExited)
            exitSignal.TrySetResult();

        _ = Task.Run(async () =>
        {
            try
            {
                await exitSignal.Task.ConfigureAwait(false);
                int exitCode;
                try { exitCode = process.ExitCode; }
                catch (InvalidOperationException) { exitCode = -1; }

                logger.LogInformation("Process {Pid} exited with code {Code}", pid, exitCode);
                onProcessExited?.Invoke(pid, exitCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to observe process {Pid}", pid);
            }
            finally
            {
                // Give the async readers a moment to flush the last lines, then
                // release the handle regardless of pipe state.
                try { await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                try { process.Dispose(); } catch { }
            }
        }, CancellationToken.None);

        return pid;
    }

    private void SafeInvoke(Action<int, string>? callback, int pid, string line)
    {
        if (callback is null) return;
        try { callback(pid, line); }
        catch (Exception ex) { logger.LogDebug(ex, "Output callback failed for PID {Pid}", pid); }
    }

    public async Task StopProcessAsync(int processId, CancellationToken ct = default)
    {
        using var activity = KastActivitySources.Process.StartActivity(
            "kast.process.stop", ActivityKind.Internal);
        activity?.SetTag("process.pid", processId);

        var process = TryGetProcess(processId);
        if (process is null)
        {
            logger.LogInformation("Process {Pid} is not running", processId);
            return;
        }

        using (process)
        {
            // PID reuse guard: a stale ServerInstance.ProcessId whose PID the OS
            // recycled must never take down an unrelated process tree.
            if (!IsKnownServerProcess(process))
            {
                logger.LogWarning("Refusing to stop PID {Pid}: process name does not match a known Arma server executable", processId);
                return;
            }

            if (HasExitedSafe(process))
                return;

            logger.LogInformation("Stopping process {Pid}", processId);

            // Only wait out the grace period when a stop request was actually
            // delivered. arma3server has no window when launched headless or
            // from a service, so CloseMainWindow is usually a no-op — waiting
            // 30 s for nothing just makes every stop slow.
            if (RequestGracefulExit(process))
            {
                using var graceCts = new CancellationTokenSource(GracePeriod);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, graceCts.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                    logger.LogInformation("Process {Pid} exited gracefully", processId);
                    return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogInformation("Process {Pid} did not exit gracefully, force-killing", processId);
                }
            }

            ct.ThrowIfCancellationRequested();

            if (!await KillAndWaitAsync(process, processId))
                throw new TimeoutException(
                    $"Process {processId} is still alive {KillWaitTimeout.TotalSeconds:F0}s after being killed.");
        }
    }

    public async Task<bool> KillProcessAsync(int processId, CancellationToken ct = default)
    {
        var process = TryGetProcess(processId);
        if (process is null)
        {
            logger.LogInformation("KillProcess: PID {Pid} is not running", processId);
            return true;
        }

        using (process)
        {
            if (HasExitedSafe(process))
                return true;

            if (!IsKnownServerProcess(process))
            {
                logger.LogWarning("Refusing to kill PID {Pid}: process name does not match a known Arma server executable", processId);
                return false;
            }

            logger.LogInformation("KillProcess: Force-killing process {Pid}", processId);
            ct.ThrowIfCancellationRequested();
            return await KillAndWaitAsync(process, processId);
        }
    }

    /// <summary>
    /// Kills the process tree and waits (bounded) for it to disappear.
    /// Returns false when the process is still alive afterwards.
    /// </summary>
    private async Task<bool> KillAndWaitAsync(Process process, int processId)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException or InvalidOperationException or NotSupportedException)
        {
            // Partial tree failures (a child already gone, access denied on a
            // descendant) must not stop us from killing the main process.
            logger.LogWarning(ex, "Tree kill of PID {Pid} reported errors; killing the main process only", processId);
            try { process.Kill(entireProcessTree: false); }
            catch (Exception inner) when (inner is Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(inner, "Kill of PID {Pid} failed", processId);
            }
        }

        using var waitCts = new CancellationTokenSource(KillWaitTimeout);
        try
        {
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            // fall through to the liveness check
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogDebug(ex, "Wait for PID {Pid} failed", processId);
        }

        var exited = HasExitedSafe(process);
        if (!exited)
            logger.LogError("Process {Pid} survived a force-kill for {Seconds:F0}s", processId, KillWaitTimeout.TotalSeconds);
        return exited;
    }

    private bool RequestGracefulExit(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return ProcessNative.TrySendTerminate(process.Id);

        try
        {
            return process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            logger.LogDebug(ex, "CloseMainWindow failed for PID {Pid}", process.Id);
            return false;
        }
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null; // not running
        }
        catch (InvalidOperationException)
        {
            return null; // exited during lookup
        }
    }

    /// <summary>
    /// HasExited that never throws. Access denied (Win32Exception) means the
    /// process exists but we cannot open it — report it as still running so a
    /// live server is never mistaken for a dead one.
    /// </summary>
    private static bool HasExitedSafe(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsKnownServerProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            return KnownServerProcessNames.Any(n =>
                name.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException)
        {
            // Process exited between resolution and the name read
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    internal static readonly string[] KnownServerProcessNames =
    [
        "arma3server_x64",
        "arma3server",
        "arma3server_x64.exe",
        "arma3server.exe"
    ];

    public bool IsProcessRunning(int processId)
    {
        using var process = TryGetProcess(processId);
        if (process is null)
            return false;

        return !HasExitedSafe(process) && IsKnownServerProcess(process);
    }

    public IReadOnlyList<KastProcessInfo> FindKastServerProcesses()
    {
        var found = new List<KastProcessInfo>();
        var seen = new HashSet<int>();

        foreach (var procName in KnownServerProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(procName); }
            catch { continue; }

            foreach (var process in processes)
            {
                using (process)
                {
                    int pid;
                    try { pid = process.Id; }
                    catch { continue; }

                    if (!seen.Add(pid) || HasExitedSafe(process))
                        continue;

                    var commandLine = ProcessNative.TryGetCommandLine(pid);
                    if (!ProcessNative.TryParseInstanceTag(commandLine, out var instanceId, out var hcIndex))
                        continue;

                    found.Add(new KastProcessInfo(
                        pid,
                        instanceId,
                        hcIndex,
                        ProcessNative.TryGetExecutablePath(pid),
                        commandLine));
                }
            }
        }

        return found;
    }

    public async Task<(double CpuPercent, long MemoryBytes)?> GetProcessMetricsAsync(int processId, CancellationToken ct = default)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return null;

            var startCpu = process.TotalProcessorTime;
            var startTime = DateTime.UtcNow;

            await Task.Delay(500, ct);

            process.Refresh();
            var endCpu = process.TotalProcessorTime;
            var endTime = DateTime.UtcNow;

            var cpuUsed = (endCpu - startCpu).TotalMilliseconds;
            var elapsed = (endTime - startTime).TotalMilliseconds;
            var cpuPercent = cpuUsed / (Environment.ProcessorCount * elapsed) * 100;

            return (cpuPercent, process.WorkingSet64);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<RunningProcessInfo>> GetRunningServerProcessesAsync(CancellationToken ct = default)
    {
        // This service is a singleton; the DbContext is scoped, so acquire a
        // dedicated scope per call instead of capturing a captive context.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KastDbContext>();

        var instances = await db.ServerInstances
            .AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.ProcessId })
            .ToListAsync(ct);
        var managedPids = instances
            .Where(s => s.ProcessId != null)
            .ToDictionary(s => s.ProcessId!.Value, s => (s.Id, s.Name));
        var instanceNames = instances.ToDictionary(s => s.Id, s => s.Name);
        var headlessPids = await db.HeadlessClients
            .AsNoTracking()
            .Where(h => h.ProcessId != null)
            .Select(h => new { h.ProcessId, h.ServerInstanceId })
            .ToListAsync(ct);
        foreach (var hc in headlessPids)
        {
            if (instanceNames.TryGetValue(hc.ServerInstanceId, out var name))
                managedPids.TryAdd(hc.ProcessId!.Value, (hc.ServerInstanceId, name));
        }

        var processes = new List<Process>();
        var seen = new HashSet<int>();
        foreach (var procName in KnownServerProcessNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    if (seen.Add(p.Id)) processes.Add(p);
                    else p.Dispose();
                }
            }
            catch { continue; }
        }

        var results = new ConcurrentBag<RunningProcessInfo>();
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            // Each sample includes a 500 ms CPU-delta wait; sample concurrently
            // so N processes do not cost N × 500 ms serially.
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
        };

        await Parallel.ForEachAsync(processes, options, async (process, token) =>
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                        return;

                    var cmdLine = ProcessNative.TryGetCommandLine(process.Id);

                    int? port = null;
                    if (cmdLine != null)
                    {
                        var portMatch = PortRegex().Match(cmdLine);
                        if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var parsedPort))
                            port = parsedPort;
                    }

                    var isManaged = managedPids.TryGetValue(process.Id, out var link);
                    double cpuPercent = 0;
                    try
                    {
                        var metrics = await GetProcessMetricsAsync(process.Id, token);
                        if (metrics.HasValue) cpuPercent = metrics.Value.CpuPercent;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }

                    results.Add(new RunningProcessInfo(
                        process.Id,
                        process.ProcessName,
                        process.StartTime,
                        cpuPercent,
                        process.WorkingSet64,
                        cmdLine,
                        port,
                        isManaged,
                        isManaged ? link.Id : null,
                        isManaged ? link.Name : null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Process may have exited between enumeration and query
                }
            }
        });

        return results.ToList();
    }

    [GeneratedRegex(@"-port=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PortRegex();
}
