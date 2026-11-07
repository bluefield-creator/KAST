using KAST.Core.Models;

namespace KAST.Core.Interfaces;

public interface IProcessManagerService
{
    Task<int> StartServerProcessAsync(string executablePath, string arguments,
        Action<int, string>? onOutputLine = null, Action<int, int>? onProcessExited = null,
        CancellationToken ct = default);

    /// <summary>
    /// Asks the process to exit (SIGTERM / close window), force-kills the whole
    /// tree after the grace period and waits a bounded time for it to die.
    /// Throws <see cref="TimeoutException"/> when the process survives the kill.
    /// </summary>
    Task StopProcessAsync(int processId, CancellationToken ct = default);

    /// <summary>Force-kills the process tree. Returns true once the process is gone.</summary>
    Task<bool> KillProcessAsync(int processId, CancellationToken ct = default);

    bool IsProcessRunning(int processId);
    Task<(double CpuPercent, long MemoryBytes)?> GetProcessMetricsAsync(int processId, CancellationToken ct = default);
    Task<IReadOnlyList<RunningProcessInfo>> GetRunningServerProcessesAsync(CancellationToken ct = default);

    /// <summary>
    /// Enumerates Arma processes whose command line carries a KAST instance
    /// tag (<c>-name=server_{id}</c>). Processes whose command line cannot be
    /// read are omitted.
    /// </summary>
    IReadOnlyList<KastProcessInfo> FindKastServerProcesses();
}
