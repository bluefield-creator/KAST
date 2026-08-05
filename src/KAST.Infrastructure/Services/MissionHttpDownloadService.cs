using KAST.Core.Interfaces;
using KAST.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KAST.Infrastructure.Services;

public class MissionHttpDownloadService(
    KastDbContext db,
    IMissionHashService hashService,
    ILogger<MissionHttpDownloadService> logger) : IMissionHttpDownloadService
{
    public async Task<MissionDownloadResult?> GetDownloadAsync(int instanceId, string fileName, CancellationToken ct = default)
    {
        // Mission downloads are opt-in per instance: refuse when disabled so the
        // endpoint cannot expose files of instances that did not enable it.
        var instance = await db.ServerInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == instanceId, ct);
        if (instance is null || !instance.HttpDownloadsEnabled)
            return null;

        var mission = await db.Missions
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ServerInstanceId == instanceId && m.FileName == fileName, ct);

        if (mission == null || string.IsNullOrEmpty(mission.PhysicalPath))
            return null;

        var fileInfo = new FileInfo(mission.PhysicalPath);
        if (!fileInfo.Exists)
            return null;

        // Stored size/hash drift when the file changes on disk (external edit,
        // HTTP-download replacement). Recompute whenever they no longer match.
        if (mission.Hash is null || mission.SizeBytes != fileInfo.Length)
        {
            mission.Hash = await hashService.ComputeHashAsync(mission.PhysicalPath, ct);
            mission.SizeBytes = fileInfo.Length;

            var tracked = await db.Missions.FindAsync([mission.Id], ct);
            if (tracked != null)
            {
                tracked.Hash = mission.Hash;
                tracked.SizeBytes = mission.SizeBytes;
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Recomputed hash for mission {FileName} on instance {InstanceId}: {Hash}",
                mission.FileName, instanceId, mission.Hash);
        }

        var lastModified = fileInfo.LastWriteTimeUtc;
        var etag = $"\"{instanceId}-{mission.FileName}-{mission.Hash}-{mission.SizeBytes}\"";

        return new MissionDownloadResult
        {
            PhysicalPath = mission.PhysicalPath,
            FileName = mission.FileName,
            SizeBytes = mission.SizeBytes,
            Hash = mission.Hash.Value,
            LastModified = lastModified,
            ETag = etag
        };
    }
}
