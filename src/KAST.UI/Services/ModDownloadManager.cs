using KAST.Core.Interfaces;

namespace KAST.UI.Services;

public sealed class ModDownloadManager(IModDownloadQueueService queue)
{
    public int ActiveCount => queue.ActiveCount;

    public bool IsActive(int modId) => queue.IsActive(modId);

    public Task<int> StartAllOutdatedAsync(CancellationToken ct = default)
        => queue.QueueAllOutdatedAsync(ct);

    public async Task<bool> StartDownloadAsync(int modId, bool isUpdate, CancellationToken ct = default)
        => await queue.QueueDownloadAsync(modId, isUpdate, ct) is not null;

    public Task<bool> CancelAsync(int modId, CancellationToken ct = default)
        => queue.CancelAsync(modId, ct);

    public Task<int> CancelAllAsync(CancellationToken ct = default)
        => queue.CancelAllAsync(ct);
}
