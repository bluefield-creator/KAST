using System.Collections.Concurrent;

namespace KAST.Infrastructure.Services;

/// <summary>
/// Process-wide, per-instance mutual exclusion for anything that changes an
/// instance's process state: start, stop, restart, the exit handler, the
/// watchdog and the orphan reaper. Without it a stop racing a start (or the
/// watchdog racing either) leaves the row in a state that no longer matches
/// the processes on the host.
/// </summary>
public static class InstanceLifecycleLocks
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Locks = new();

    private static SemaphoreSlim For(int instanceId)
        => Locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Waits for exclusive ownership of the instance's lifecycle.</summary>
    public static async Task<IDisposable> EnterAsync(int instanceId, CancellationToken ct = default)
    {
        var gate = For(instanceId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gate);
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/>; returns null when the instance
    /// is still busy afterwards.
    /// </summary>
    public static async Task<IDisposable?> TryEnterAsync(int instanceId, TimeSpan timeout, CancellationToken ct = default)
    {
        var gate = For(instanceId);
        return await gate.WaitAsync(timeout, ct).ConfigureAwait(false) ? new Releaser(gate) : null;
    }

    /// <summary>True while a lifecycle operation holds the instance.</summary>
    public static bool IsBusy(int instanceId)
        => Locks.TryGetValue(instanceId, out var gate) && gate.CurrentCount == 0;

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
