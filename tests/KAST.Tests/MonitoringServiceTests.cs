using KAST.Core.Enums;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Services;
using KAST.Tests.Helpers;

namespace KAST.Tests;

public class MonitoringServiceTests : IDisposable
{
    private readonly Infrastructure.Data.KastDbContext _db = DbHelper.CreateInMemoryDb();

    [Fact]
    public async Task GetAllInstanceMetricsAsync_SamplesProcessesConcurrently()
    {
        _db.ServerInstances.AddRange(
            new ServerInstance { Name = "A", ProcessId = 1001, Status = ServerInstanceStatus.Running },
            new ServerInstance { Name = "B", ProcessId = 1002, Status = ServerInstanceStatus.Running },
            new ServerInstance { Name = "C", ProcessId = 1003, Status = ServerInstanceStatus.Running });
        await _db.SaveChangesAsync();
        var processManager = new GatedProcessManager();
        var sut = new MonitoringService(processManager, _db);

        var metricsTask = sut.GetAllInstanceMetricsAsync();

        // Deterministic gate: all samples must have entered before any may
        // complete — overlap is guaranteed, not timing-dependent.
        await WaitUntilAsync(() => processManager.Entered == 3);
        processManager.ReleaseAll();

        var metrics = await metricsTask;

        Assert.Equal(3, metrics.Count);
        Assert.True(processManager.MaxConcurrentCalls > 1);
    }

    public void Dispose() => _db.Dispose();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException("Condition was not met.");

            await Task.Delay(25);
        }
    }

    private sealed class GatedProcessManager : IProcessManagerService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;
        private int _entered;

        public int Entered => _entered;
        public int MaxConcurrentCalls { get; private set; }

        public async Task<(double CpuPercent, long MemoryBytes)?> GetProcessMetricsAsync(int processId, CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            Interlocked.Increment(ref _entered);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, active);
            try
            {
                // Block until the test releases all samples.
                await _release.Task.WaitAsync(ct);
                return (processId / 100.0, processId);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void ReleaseAll() => _release.TrySetResult();

        public Task<int> StartServerProcessAsync(string executablePath, string arguments, Action<int, string>? onOutputLine = null,
            Action<int, int>? onProcessExited = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task StopProcessAsync(int processId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> KillProcessAsync(int processId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<RunningProcessInfo>> GetRunningServerProcessesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public bool IsProcessRunning(int processId) => true;
    }
}
