using System.Collections.Concurrent;
using KAST.Core.Enums;
using KAST.Core.Models;

namespace KAST.Infrastructure.Services.Content;

/// <summary>
/// Singleton store mapping content keys to their live install state.
/// Keys: "server:{instanceId}", "mod:{modId}"
/// </summary>
public class ContentProgressTracker
{
    /// <summary>
    /// Terminal states are kept visible for this long (so the UI can show the
    /// final result) and then evicted — the store must not grow without bound.
    /// </summary>
    private static readonly TimeSpan CompletedStateTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, ContentInstallState> _states = new();
    private readonly ConcurrentDictionary<string, DateTime> _completedAt = new();

    public static string ServerKey(int instanceId) => $"server:{instanceId}";
    public static string ModKey(int modId) => $"mod:{modId}";

    public ContentInstallState Create(string key, ContentType type, string label, IReadOnlyList<ContentStep> steps)
    {
        var state = new ContentInstallState
        {
            Key = key,
            Type = type,
            Label = label,
            Steps = steps.ToList()
        };
        _states[key] = state;
        _completedAt.TryRemove(key, out _);
        return state;
    }

    public void Set(ContentInstallState state)
    {
        _states[state.Key] = state;

        if (IsTerminal(state))
            _completedAt[state.Key] = DateTime.UtcNow;
        else
            _completedAt.TryRemove(state.Key, out _);

        PruneCompleted();
    }

    public ContentInstallState? Get(string key)
    {
        if (!_states.TryGetValue(key, out var state))
            return null;

        // Record the completion moment once so TTL eviction has a stable basis.
        if (IsTerminal(state))
            _completedAt.TryAdd(key, DateTime.UtcNow);

        PruneCompleted();

        // The state may have been evicted above — do not hand out a stale entry.
        return _states.TryGetValue(key, out var current) ? current : null;
    }

    public IReadOnlyList<ContentInstallState> GetAll()
    {
        PruneCompleted();
        return _states.Values.ToList();
    }

    private static bool IsTerminal(ContentInstallState state)
        => state.IsComplete || state.ErrorMessage is not null;

    private void PruneCompleted()
    {
        var cutoff = DateTime.UtcNow - CompletedStateTtl;
        foreach (var (key, completedAt) in _completedAt)
        {
            if (completedAt >= cutoff)
                continue;

            _completedAt.TryRemove(key, out _);
            _states.TryRemove(key, out _);
        }
    }
}
