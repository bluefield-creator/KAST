using KAST.Core.Models;

namespace KAST.Core.Interfaces;

public interface IStorageService
{
    Task<StorageScanResult> ScanStorageAsync(StorageScanRequest request, CancellationToken ct = default);
    Task<StorageApplyResult> ApplyStorageChangesAsync(StorageApplyRequest request, CancellationToken ct = default);
    Task<StorageMigrationResult> MigrateStorageAsync(StorageMigrationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Re-links servers and mods whose recorded folder no longer exists into the
    /// current storage directories. With <paramref name="onlyWhenTargetExists"/>
    /// only records whose folder is present at the new location are changed.
    /// </summary>
    Task<StorageApplyResult> RelinkMissingPathsAsync(bool onlyWhenTargetExists, CancellationToken ct = default);
}
