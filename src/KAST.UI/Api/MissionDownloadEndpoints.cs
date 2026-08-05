using KAST.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KAST.UI.Api;

public static class MissionDownloadEndpoints
{
    public static IEndpointRouteBuilder MapMissionDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/mission-download/{instanceId:int}/{**filename}", GetMissionDownloadAsync)
            .RequireRateLimiting("mission-download");
        return endpoints;
    }

    private static async Task<IResult> GetMissionDownloadAsync(
        int instanceId,
        string filename,
        HttpContext http,
        IMissionHttpDownloadService downloadService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filename) || !filename.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        var result = await downloadService.GetDownloadAsync(instanceId, filename, ct);
        if (result == null)
            return Results.NotFound();

        var fileInfo = new FileInfo(result.PhysicalPath);
        if (!fileInfo.Exists)
            return Results.NotFound();

        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

        // Cache headers are part of the cache contract — emit them on the
        // 304 path too, or clients that revalidate lose the file metadata.
        http.Response.Headers["X-Hash"] = result.Hash.ToString();
        http.Response.Headers["X-BSize"] = result.SizeBytes.ToString();

        // Check If-Modified-Since
        if (http.Request.Headers.IfModifiedSince is { Count: > 0 } imsValues
            && DateTimeOffset.TryParse(imsValues.ToString(), out var ifModifiedSince)
            && lastModified <= ifModifiedSince)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // Check If-None-Match (ETag)
        if (http.Request.Headers.IfNoneMatch is { Count: > 0 } inmValues
            && inmValues.ToString() == result.ETag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // Log the download. Player headers are attacker-controlled — trim them
        // so a hostile client cannot flood the log with arbitrary text.
        var playerName = Cap(http.Request.Headers["Player-Name"].FirstOrDefault());
        var playerSteamId = Cap(http.Request.Headers["Player-Steamid"].FirstOrDefault());
        var serverAddress = Cap(http.Request.Headers["Server-Address"].FirstOrDefault());
        var userAgent = Cap(http.Request.Headers.UserAgent.FirstOrDefault());

        var logger = loggerFactory.CreateLogger("KAST.UI.Api.MissionDownloadEndpoints");
        logger.LogInformation(
            "HTTP mission download: Instance={InstanceId}, Mission={Mission}, Player={PlayerName}, SteamId={SteamId}, Server={ServerAddress}, UA={UserAgent}",
            instanceId, filename, playerName, playerSteamId, serverAddress, userAgent);

        return Results.File(
            result.PhysicalPath,
            "application/octet-stream",
            lastModified: lastModified,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(result.ETag),
            enableRangeProcessing: true);
    }

    private static string? Cap(string? value)
    {
        if (value is null)
            return null;
        return value.Length <= 128 ? value : value[..128];
    }
}
