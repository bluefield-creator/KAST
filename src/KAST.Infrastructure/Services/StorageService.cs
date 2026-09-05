using System.Security.Cryptography;
using System.Text;
using KAST.Core.Enums;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace KAST.Infrastructure.Services;

public sealed class StorageService(
    KastDbContext db,
    ISettingsService settingsService,
    IHostEnvironment hostEnvironment) : IStorageService
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task<StorageScanResult> ScanStorageAsync(StorageScanRequest request, CancellationToken ct = default)
    {
        var settings = await settingsService.GetSettingsAsync(ct);
        var modsDirectory = ResolvePath(request.ModsDirectory);
        var serversDirectory = ResolvePath(request.ServersDirectory);

        var servers = await ScanServersAsync(serversDirectory, ct);
        var mods = await ScanModsAsync(modsDirectory, ct);
        var relinks = await FindRelinksAsync(
            settings.ServersDirectory, serversDirectory,
            settings.ModsDirectory, modsDirectory,
            servers.Concat(mods).Where(c => c.ExistingId is not null).Select(c => (c.Kind, c.ExistingId!.Value)).ToHashSet(),
            ct);

        return new StorageScanResult(
            settings.ModsDirectory,
            settings.ServersDirectory,
            modsDirectory,
            serversDirectory,
            servers,
            mods,
            relinks);
    }

    /// <summary>
    /// Records that live under the current storage directory but whose folder
    /// is gone are re-based onto the proposed directory by relative path. This
    /// is what "I moved the data directory" means for everything the scan
    /// could not pair up: uninstalled servers, folders without the server
    /// executable, names that differ from the record. Records the scan already
    /// matched are left to their resolutions.
    /// </summary>
    private async Task<IReadOnlyList<StorageRelink>> FindRelinksAsync(
        string currentServersDirectory,
        string proposedServersDirectory,
        string currentModsDirectory,
        string proposedModsDirectory,
        HashSet<(StorageCandidateKind Kind, int Id)> matched,
        CancellationToken ct)
    {
        var relinks = new List<StorageRelink>();

        foreach (var server in await db.ServerInstances.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct))
        {
            if (matched.Contains((StorageCandidateKind.Server, server.Id)))
                continue;
            if (TryRebase(server.InstallPath, currentServersDirectory, proposedServersDirectory) is { } newPath)
                relinks.Add(new StorageRelink(StorageCandidateKind.Server, server.Id, server.Name, server.InstallPath, newPath, Directory.Exists(newPath)));
        }

        foreach (var mod in await db.Mods.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct))
        {
            if (matched.Contains((StorageCandidateKind.Mod, mod.Id)))
                continue;
            if (TryRebase(mod.LocalPath, currentModsDirectory, proposedModsDirectory) is { } newPath)
                relinks.Add(new StorageRelink(StorageCandidateKind.Mod, mod.Id, mod.Name, mod.LocalPath!, newPath, Directory.Exists(newPath)));
        }

        return relinks;
    }

    /// <summary>
    /// Re-links records whose folder no longer exists into the <em>current</em>
    /// storage directories, for installs where the directory setting was
    /// already changed while the records kept their old paths. With
    /// <paramref name="onlyWhenTargetExists"/> only records whose folder is
    /// actually present at the new location are touched — safe to run
    /// unattended at startup.
    /// </summary>
    public async Task<StorageApplyResult> RelinkMissingPathsAsync(bool onlyWhenTargetExists, CancellationToken ct = default)
    {
        var settings = await settingsService.GetSettingsAsync(ct);
        var relinks = await FindRelinksAsync(
            settings.ServersDirectory, settings.ServersDirectory,
            settings.ModsDirectory, settings.ModsDirectory,
            [],
            ct);
        if (onlyWhenTargetExists)
            relinks = relinks.Where(r => r.NewPathExists).ToList();

        var (servers, mods) = await ApplyRelinksAsync(relinks, ct);
        if (servers + mods > 0)
            await db.SaveChangesAsync(ct);

        return new StorageApplyResult(0, 0, 0, 0, false, servers, mods);
    }

    private async Task<(int Servers, int Mods)> ApplyRelinksAsync(IReadOnlyList<StorageRelink> relinks, CancellationToken ct)
    {
        var serversRelinked = 0;
        var modsRelinked = 0;
        foreach (var relink in relinks)
        {
            ct.ThrowIfCancellationRequested();
            if (relink.Kind == StorageCandidateKind.Server)
            {
                var server = await db.ServerInstances.FindAsync([relink.Id], ct);
                if (server is null || !PathsEqual(server.InstallPath, relink.CurrentPath) || Directory.Exists(server.InstallPath))
                    continue;
                server.InstallPath = relink.NewPath;
                server.LastModified = DateTime.UtcNow;
                serversRelinked++;
            }
            else
            {
                var mod = await db.Mods.FindAsync([relink.Id], ct);
                if (mod is null || string.IsNullOrWhiteSpace(mod.LocalPath) || !PathsEqual(mod.LocalPath, relink.CurrentPath) || Directory.Exists(mod.LocalPath))
                    continue;
                mod.LocalPath = relink.NewPath;
                if (Directory.Exists(relink.NewPath))
                {
                    mod.SizeBytes = GetDirectorySize(relink.NewPath);
                    mod.Status = ModStatus.Installed;
                    mod.LastUpdatedLocal = DateTime.UtcNow;
                }
                modsRelinked++;
            }
        }

        return (serversRelinked, modsRelinked);
    }

    /// <summary>
    /// Where a record whose folder is gone should live under
    /// <paramref name="newRoot"/>: its old-root-relative path when it sat
    /// under <paramref name="oldRoot"/>, otherwise its leaf folder name (a
    /// record left behind on a drive the directory already moved away from).
    /// Null when the folder still exists — data that is still there stays
    /// put — or when nothing would change.
    /// </summary>
    private static string? TryRebase(string? path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newRoot))
            return null;

        var full = NormalizePath(path);
        if (Directory.Exists(full))
            return null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var oldFull = string.IsNullOrWhiteSpace(oldRoot) ? null : EnsureTrailingSeparator(NormalizePath(oldRoot));
        var relative = oldFull is not null && full.StartsWith(oldFull, comparison)
            ? Path.GetRelativePath(oldFull, full)
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return null;

        var rebased = NormalizePath(Path.Combine(newRoot, relative));
        return string.Equals(rebased, full, comparison) ? null : rebased;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task<StorageApplyResult> ApplyStorageChangesAsync(StorageApplyRequest request, CancellationToken ct = default)
    {
        var modsDirectory = ResolvePath(request.ModsDirectory);
        var serversDirectory = ResolvePath(request.ServersDirectory);
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(serversDirectory);

        var scan = await ScanStorageAsync(new StorageScanRequest(modsDirectory, serversDirectory), ct);
        var serversByPath = scan.Servers.ToDictionary(c => NormalizePath(c.Path), PathComparer);
        var modsByPath = scan.Mods.ToDictionary(c => NormalizePath(c.Path), PathComparer);

        var serversAdopted = 0;
        var serversSwitched = 0;
        var modsAdopted = 0;
        var modsSwitched = 0;

        foreach (var resolution in request.Resolutions)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizePath(resolution.DiscoveredPath);
            if (resolution.Action is StorageResolutionAction.Skip or StorageResolutionAction.KeepCurrent)
                continue;

            if (resolution.Kind == StorageCandidateKind.Server &&
                serversByPath.TryGetValue(path, out var serverCandidate))
            {
                if (resolution.Action == StorageResolutionAction.UseDiscovered &&
                    resolution.ExistingId is { } serverId)
                {
                    var existing = await db.ServerInstances.FindAsync([serverId], ct);
                    if (existing is not null)
                    {
                        existing.InstallPath = serverCandidate.Path;
                        existing.LastModified = DateTime.UtcNow;
                        serversSwitched++;
                    }
                }
                else if (resolution.Action == StorageResolutionAction.AdoptDiscovered &&
                         serverCandidate.Status is StorageCandidateStatus.New or StorageCandidateStatus.SameNameDifferentData)
                {
                    db.ServerInstances.Add(new ServerInstance
                    {
                        Name = serverCandidate.Name,
                        InstallPath = serverCandidate.Path,
                        Status = ServerInstanceStatus.Stopped,
                        InstalledAt = DateTime.UtcNow,
                        InstalledBuildId = "adopted"
                    });
                    serversAdopted++;
                }
            }

            if (resolution.Kind == StorageCandidateKind.Mod &&
                modsByPath.TryGetValue(path, out var modCandidate))
            {
                if (resolution.Action == StorageResolutionAction.UseDiscovered &&
                    resolution.ExistingId is { } modId)
                {
                    var existing = await db.Mods.FindAsync([modId], ct);
                    if (existing is not null)
                    {
                        existing.LocalPath = modCandidate.Path;
                        existing.SizeBytes = modCandidate.SizeBytes;
                        existing.Status = ModStatus.Installed;
                        existing.LastUpdatedLocal = DateTime.UtcNow;
                        modsSwitched++;
                    }
                }
                else if (resolution.Action == StorageResolutionAction.AdoptDiscovered &&
                         (modCandidate.Status == StorageCandidateStatus.New ||
                          modCandidate is { Status: StorageCandidateStatus.SameNameDifferentData, WorkshopId: 0 }))
                {
                    db.Mods.Add(new SteamMod
                    {
                        Name = modCandidate.Name,
                        WorkshopId = modCandidate.WorkshopId,
                        Source = modCandidate.WorkshopId > 0 ? ModSource.SteamWorkshop : ModSource.LocalFolder,
                        Status = ModStatus.Installed,
                        LocalPath = modCandidate.Path,
                        SizeBytes = modCandidate.SizeBytes,
                        LastUpdatedLocal = DateTime.UtcNow
                    });
                    modsAdopted++;
                }
            }
        }

        // Everything the scan could not pair up whose folder is gone follows
        // the directory to its new location.
        var (serversRelinked, modsRelinked) = await ApplyRelinksAsync(scan.Relinks, ct);

        var settings = await settingsService.GetSettingsAsync(ct);
        settings.ModsDirectory = modsDirectory;
        settings.ServersDirectory = serversDirectory;
        await settingsService.UpdateSettingsAsync(settings, ct);
        await db.SaveChangesAsync(ct);

        return new StorageApplyResult(serversAdopted, serversSwitched, modsAdopted, modsSwitched, true, serversRelinked, modsRelinked);
    }

    public async Task<StorageMigrationResult> MigrateStorageAsync(StorageMigrationRequest request, CancellationToken ct = default)
    {
        var modsDirectory = ResolvePath(request.ModsDirectory);
        var serversDirectory = ResolvePath(request.ServersDirectory);
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(serversDirectory);

        var skipped = new List<string>();
        var serversMigrated = 0;
        var modsMigrated = 0;
        var current = await settingsService.GetSettingsAsync(ct);

        var servers = await db.ServerInstances.OrderBy(s => s.Id).ToListAsync(ct);
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(server.InstallPath) || !Directory.Exists(server.InstallPath))
            {
                // Nothing to copy — but a record under the old directory still
                // follows the directory, so it is not left pointing at a dead path.
                if (TryRebase(server.InstallPath, current.ServersDirectory, serversDirectory) is { } rebased)
                {
                    server.InstallPath = rebased;
                    server.LastModified = DateTime.UtcNow;
                    skipped.Add($"Server {server.Name}: source path missing; re-linked to the new directory.");
                }
                else
                {
                    skipped.Add($"Server {server.Name}: source path missing.");
                }
                continue;
            }

            var destination = Path.Combine(serversDirectory, SafeName(Path.GetFileName(server.InstallPath), server.Name));
            if (!await TryCopyDirectoryAndVerifyAsync(server.InstallPath, destination, skipped, $"Server {server.Name}", ct))
                continue;

            server.InstallPath = destination;
            server.LastModified = DateTime.UtcNow;
            serversMigrated++;
        }

        var mods = await db.Mods.OrderBy(m => m.Id).ToListAsync(ct);
        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(mod.LocalPath) || !Directory.Exists(mod.LocalPath))
            {
                if (TryRebase(mod.LocalPath, current.ModsDirectory, modsDirectory) is { } rebased)
                {
                    mod.LocalPath = rebased;
                    if (Directory.Exists(rebased))
                    {
                        mod.SizeBytes = GetDirectorySize(rebased);
                        mod.Status = ModStatus.Installed;
                        mod.LastUpdatedLocal = DateTime.UtcNow;
                    }
                    skipped.Add($"Mod {mod.Name}: source path missing; re-linked to the new directory.");
                }
                else
                {
                    skipped.Add($"Mod {mod.Name}: source path missing.");
                }
                continue;
            }

            var folderName = mod.WorkshopId > 0 ? mod.WorkshopId.ToString() : Path.GetFileName(mod.LocalPath);
            var destination = Path.Combine(modsDirectory, SafeName(folderName, mod.Name));
            if (!await TryCopyDirectoryAndVerifyAsync(mod.LocalPath, destination, skipped, $"Mod {mod.Name}", ct))
                continue;

            mod.LocalPath = destination;
            mod.SizeBytes = GetDirectorySize(destination);
            mod.LastUpdatedLocal = DateTime.UtcNow;
            modsMigrated++;
        }

        var settings = await settingsService.GetSettingsAsync(ct);
        settings.ModsDirectory = modsDirectory;
        settings.ServersDirectory = serversDirectory;
        await settingsService.UpdateSettingsAsync(settings, ct);
        await db.SaveChangesAsync(ct);

        return new StorageMigrationResult(serversMigrated, modsMigrated, skipped, modsDirectory, serversDirectory);
    }

    private async Task<IReadOnlyList<StorageScanCandidate>> ScanServersAsync(string serversDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(serversDirectory))
            return [];

        var existing = await db.ServerInstances.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
        var existingByName = existing
            .GroupBy(s => NormalizeName(s.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Servers are installed under a sanitised folder name that can differ from
        // the display name — match on the recorded install folder as well.
        var existingByFolder = existing
            .Where(s => !string.IsNullOrWhiteSpace(s.InstallPath))
            .GroupBy(s => NormalizeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(s.InstallPath))), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var candidates = new List<StorageScanCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(serversDirectory).OrderBy(Path.GetFileName))
        {
            ct.ThrowIfCancellationRequested();
            if (!HasServerExecutable(directory))
                continue;

            var name = Path.GetFileName(directory);
            var identity = NormalizeName(name);
            var size = GetDirectorySize(directory);
            var fingerprint = "";
            var status = StorageCandidateStatus.New;
            ServerInstance? match = null;

            if (existingByName.TryGetValue(identity, out match) || existingByFolder.TryGetValue(identity, out match))
                (status, fingerprint) = await ClassifyAgainstExistingAsync(directory, match.InstallPath, ct);
            else
                fingerprint = await CalculateDirectoryFingerprintAsync(directory, ct);

            candidates.Add(new StorageScanCandidate
            {
                Kind = StorageCandidateKind.Server,
                Status = status,
                Name = name,
                Path = NormalizePath(directory),
                Identity = identity,
                Fingerprint = fingerprint,
                SizeBytes = size,
                ExistingId = match?.Id,
                ExistingPath = match?.InstallPath,
                Detail = DescribeMatch(status, "server", match?.Name)
            });
        }

        return candidates;
    }

    private async Task<IReadOnlyList<StorageScanCandidate>> ScanModsAsync(string modsDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(modsDirectory))
            return [];

        var existing = await db.Mods.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        var existingByWorkshopId = existing
            .Where(m => m.WorkshopId > 0)
            .GroupBy(m => m.WorkshopId)
            .ToDictionary(g => g.Key, g => g.First());
        var existingByName = existing
            .GroupBy(m => NormalizeName(m.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Local-folder mods are keyed by their on-disk folder, which need not
        // equal the display name.
        var existingByFolder = existing
            .Where(m => m.WorkshopId == 0 && !string.IsNullOrWhiteSpace(m.LocalPath))
            .GroupBy(m => NormalizeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(m.LocalPath!))), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var candidates = new List<StorageScanCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(modsDirectory).OrderBy(Path.GetFileName))
        {
            ct.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(directory);
            var workshopId = long.TryParse(folderName, out var parsed) && parsed > 0 ? parsed : 0;
            var name = workshopId > 0
                ? existingByWorkshopId.TryGetValue(workshopId, out var named) ? named.Name : folderName
                : folderName;
            var identity = workshopId > 0 ? $"workshop:{workshopId}" : NormalizeName(name);
            var size = GetDirectorySize(directory);
            var fingerprint = "";
            var status = StorageCandidateStatus.New;
            SteamMod? match = null;

            var matched = workshopId > 0
                ? existingByWorkshopId.TryGetValue(workshopId, out match)
                : existingByName.TryGetValue(NormalizeName(name), out match) ||
                  existingByFolder.TryGetValue(NormalizeName(folderName), out match);

            if (matched)
                (status, fingerprint) = await ClassifyAgainstExistingAsync(directory, match!.LocalPath, ct);
            else
                fingerprint = await CalculateDirectoryFingerprintAsync(directory, ct);

            candidates.Add(new StorageScanCandidate
            {
                Kind = StorageCandidateKind.Mod,
                Status = status,
                Name = name,
                Path = NormalizePath(directory),
                Identity = identity,
                Fingerprint = fingerprint,
                SizeBytes = size,
                WorkshopId = workshopId,
                ExistingId = match?.Id,
                ExistingPath = match?.LocalPath,
                Detail = DescribeMatch(status, "mod", match?.Name)
            });
        }

        return candidates;
    }

    /// <summary>
    /// Classifies a discovered folder against the record it matches. When the
    /// recorded path is gone there is nothing to compare against — the data was
    /// moved — so the folder is reported as <see cref="StorageCandidateStatus.Relocated"/>
    /// without walking it at all.
    /// </summary>
    private static async Task<(StorageCandidateStatus Status, string Fingerprint)> ClassifyAgainstExistingAsync(
        string discovered,
        string? existingPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(existingPath) || !Directory.Exists(existingPath))
            return (StorageCandidateStatus.Relocated, "");

        var fingerprint = await CalculateDirectoryFingerprintAsync(discovered, ct);
        var existingFingerprint = await CalculateDirectoryFingerprintAsync(existingPath, ct);
        var status = string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal)
            ? StorageCandidateStatus.IdenticalDuplicate
            : StorageCandidateStatus.SameNameDifferentData;
        return (status, fingerprint);
    }

    private static string DescribeMatch(StorageCandidateStatus status, string kind, string? existingName) => status switch
    {
        StorageCandidateStatus.New => $"Discovered {kind} folder.",
        StorageCandidateStatus.Relocated => $"Matches {kind} {existingName}, whose current path no longer exists.",
        _ => $"Matches existing {kind} {existingName}."
    };

    private async Task<bool> TryCopyDirectoryAndVerifyAsync(
        string source,
        string destination,
        List<string> skipped,
        string label,
        CancellationToken ct)
    {
        if (Directory.Exists(destination))
        {
            skipped.Add($"{label}: destination already exists.");
            return false;
        }

        var sourceRoot = EnsureTrailingSeparator(Path.GetFullPath(source));
        var destinationRoot = EnsureTrailingSeparator(Path.GetFullPath(destination));
        if (destinationRoot.StartsWith(sourceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            skipped.Add($"{label}: destination is inside the source path.");
            return false;
        }

        // Verify the copy structurally (path, size, write time). The copy itself
        // throws on any I/O failure; re-reading hundreds of gigabytes to hash
        // both sides again would triple the cost of an already long migration.
        var sourceFingerprint = await CalculateDirectoryFingerprintAsync(source, ct);
        try
        {
            await CopyDirectoryAsync(source, destination, ct);
            var destinationFingerprint = await CalculateDirectoryFingerprintAsync(destination, ct);
            if (string.Equals(sourceFingerprint, destinationFingerprint, StringComparison.Ordinal))
                return true;
        }
        catch
        {
            TryDeleteDirectory(destination);
            throw;
        }

        // Never leave a partially copied destination behind
        TryDeleteDirectory(destination);
        skipped.Add($"{label}: copy verification failed.");
        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            {
                await using var sourceStream = File.OpenRead(file);
                await using var targetStream = File.Create(target);
                await sourceStream.CopyToAsync(targetStream, ct);
            }
            // Stamp the write time only after the stream is closed — closing a
            // written file would otherwise overwrite it with "now".
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    private string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(hostEnvironment.ContentRootPath, expanded));
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private static bool HasServerExecutable(string directory)
        => File.Exists(Path.Combine(directory, "arma3server_x64.exe")) ||
           File.Exists(Path.Combine(directory, "arma3server_x64"));

    private static string NormalizeName(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            .ToArray();
        return chars.Length == 0 ? "item" : new string(chars);
    }

    private static string SafeName(string? preferred, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "item" : value;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    /// <summary>
    /// Cheap structural fingerprint for scans: relative path, length and
    /// last-write time of every file. O(files), never reads file contents —
    /// an Arma install plus a mod set is tens to hundreds of gigabytes, and
    /// the scan runs on every storage change (twice, counting Apply).
    /// Explorer, robocopy and <see cref="CopyDirectoryAsync"/> all preserve
    /// write times, so a faithful copy fingerprints identically.
    /// </summary>
    private static Task<string> CalculateDirectoryFingerprintAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return Task.FromResult("");

        var separator = new byte[] { 0 };
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                     .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/')));
            sha.AppendData(separator);
            sha.AppendData(BitConverter.GetBytes(info.Length));
            sha.AppendData(separator);
            sha.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
            sha.AppendData(separator);
        }

        return Task.FromResult(Convert.ToHexString(sha.GetHashAndReset()));
    }

}
