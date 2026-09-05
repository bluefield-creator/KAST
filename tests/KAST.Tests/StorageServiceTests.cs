using KAST.Core.Enums;
using KAST.Core.Models;
using KAST.Infrastructure.Services;
using KAST.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace KAST.Tests;

public sealed class StorageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kast-storage-tests-{Guid.NewGuid():N}");
    private readonly Infrastructure.Data.KastDbContext _db;
    private readonly StorageService _sut;

    public StorageServiceTests()
    {
        Directory.CreateDirectory(_root);
        _db = DbHelper.CreateInMemoryDb();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kast:ModsDirectory"] = Path.Combine(_root, "mods-old"),
                ["Kast:ServersDirectory"] = Path.Combine(_root, "servers-old")
            })
            .Build();
        var environment = new TestHostEnvironment(_root);
        var settings = new SettingsService(_db, config, environment);
        _sut = new StorageService(_db, settings, environment);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ScanStorage_FindsServerAndWorkshopModCandidates()
    {
        var servers = Path.Combine(_root, "servers-new");
        var mods = Path.Combine(_root, "mods-new");
        CreateServer(servers, "Alpha", "same");
        CreateMod(mods, "12345", "same");

        var result = await _sut.ScanStorageAsync(new StorageScanRequest(mods, servers));

        var server = Assert.Single(result.Servers);
        Assert.Equal("Alpha", server.Name);
        Assert.Equal(StorageCandidateStatus.New, server.Status);

        var mod = Assert.Single(result.Mods);
        Assert.Equal(12345, mod.WorkshopId);
        Assert.Equal(StorageCandidateStatus.New, mod.Status);
    }

    [Fact]
    public async Task ScanStorage_DetectsIdenticalDuplicateAndDifferentDataConflict()
    {
        var oldServers = Path.Combine(_root, "servers-old");
        var newServers = Path.Combine(_root, "servers-new");
        var oldMods = Path.Combine(_root, "mods-old");
        var newMods = Path.Combine(_root, "mods-new");
        var oldServerPath = CreateServer(oldServers, "Alpha", "same");
        var oldModPath = CreateMod(oldMods, "12345", "same");
        CreateServer(newServers, "Alpha", "same");
        CreateMod(newMods, "12345", "same");

        _db.ServerInstances.Add(new ServerInstance { Name = "Alpha", InstallPath = oldServerPath });
        _db.Mods.Add(new SteamMod
        {
            Name = "Existing Mod",
            WorkshopId = 12345,
            LocalPath = oldModPath,
            Status = ModStatus.Installed
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ScanStorageAsync(new StorageScanRequest(newMods, newServers));

        Assert.Equal(StorageCandidateStatus.IdenticalDuplicate, Assert.Single(result.Servers).Status);
        Assert.Equal(StorageCandidateStatus.IdenticalDuplicate, Assert.Single(result.Mods).Status);

        File.WriteAllText(Path.Combine(newServers, "Alpha", "changed.txt"), "different");
        File.WriteAllText(Path.Combine(newMods, "12345", "changed.txt"), "different");

        result = await _sut.ScanStorageAsync(new StorageScanRequest(newMods, newServers));

        Assert.Equal(StorageCandidateStatus.SameNameDifferentData, Assert.Single(result.Servers).Status);
        Assert.Equal(StorageCandidateStatus.SameNameDifferentData, Assert.Single(result.Mods).Status);
    }

    [Fact]
    public async Task ScanStorage_ReportsRelocatedWhenRecordedPathIsGone()
    {
        // The admin moved the whole data directory: records still point at the
        // old location, which no longer exists.
        var goneServers = Path.Combine(_root, "servers-gone");
        var goneMods = Path.Combine(_root, "mods-gone");
        var newServers = Path.Combine(_root, "servers-new");
        var newMods = Path.Combine(_root, "mods-new");
        var newServerPath = CreateServer(newServers, "Alpha", "same");
        var newModPath = CreateMod(newMods, "12345", "same");
        var server = new ServerInstance { Name = "Alpha", InstallPath = Path.Combine(goneServers, "Alpha") };
        var mod = new SteamMod { Name = "Existing Mod", WorkshopId = 12345, LocalPath = Path.Combine(goneMods, "12345") };
        _db.ServerInstances.Add(server);
        _db.Mods.Add(mod);
        await _db.SaveChangesAsync();

        var result = await _sut.ScanStorageAsync(new StorageScanRequest(newMods, newServers));

        var serverCandidate = Assert.Single(result.Servers);
        Assert.Equal(StorageCandidateStatus.Relocated, serverCandidate.Status);
        Assert.Equal(server.Id, serverCandidate.ExistingId);
        Assert.Equal(newServerPath, serverCandidate.Path);

        var modCandidate = Assert.Single(result.Mods);
        Assert.Equal(StorageCandidateStatus.Relocated, modCandidate.Status);
        Assert.Equal(mod.Id, modCandidate.ExistingId);
        Assert.Equal(newModPath, modCandidate.Path);
    }

    [Fact]
    public async Task ScanStorage_MatchesServerByInstallFolderWhenDisplayNameDiffers()
    {
        var goneServers = Path.Combine(_root, "servers-gone");
        var newServers = Path.Combine(_root, "servers-new");
        CreateServer(newServers, "Main_Server", "same");
        var server = new ServerInstance { Name = "Main Server (EU)", InstallPath = Path.Combine(goneServers, "Main_Server") };
        _db.ServerInstances.Add(server);
        await _db.SaveChangesAsync();

        var result = await _sut.ScanStorageAsync(new StorageScanRequest(Path.Combine(_root, "mods-new"), newServers));

        var candidate = Assert.Single(result.Servers);
        Assert.Equal(StorageCandidateStatus.Relocated, candidate.Status);
        Assert.Equal(server.Id, candidate.ExistingId);
    }

    [Fact]
    public async Task ApplyStorageChanges_RelocatedUseDiscoveredRepointsRecordsWithoutOldData()
    {
        var goneServers = Path.Combine(_root, "servers-gone");
        var goneMods = Path.Combine(_root, "mods-gone");
        var newServers = Path.Combine(_root, "servers-new");
        var newMods = Path.Combine(_root, "mods-new");
        var newServerPath = CreateServer(newServers, "Alpha", "same");
        var newModPath = CreateMod(newMods, "12345", "same");
        var server = new ServerInstance { Name = "Alpha", InstallPath = Path.Combine(goneServers, "Alpha") };
        var mod = new SteamMod { Name = "Existing Mod", WorkshopId = 12345, LocalPath = Path.Combine(goneMods, "12345"), Status = ModStatus.NotInstalled };
        _db.ServerInstances.Add(server);
        _db.Mods.Add(mod);
        await _db.SaveChangesAsync();

        var applied = await _sut.ApplyStorageChangesAsync(new StorageApplyRequest(
            newMods,
            newServers,
            [
                new(StorageCandidateKind.Server, newServerPath, StorageResolutionAction.UseDiscovered, server.Id),
                new(StorageCandidateKind.Mod, newModPath, StorageResolutionAction.UseDiscovered, mod.Id)
            ]));

        Assert.Equal(1, applied.ServersSwitched);
        Assert.Equal(1, applied.ModsSwitched);
        Assert.Equal(newServerPath, _db.ServerInstances.Single().InstallPath);
        Assert.Equal(newModPath, _db.Mods.Single().LocalPath);
        Assert.Equal(ModStatus.Installed, _db.Mods.Single().Status);
    }

    [Fact]
    public async Task ApplyStorageChanges_UseDiscoveredSwitchesExistingPaths()
    {
        var oldServers = Path.Combine(_root, "servers-old");
        var newServers = Path.Combine(_root, "servers-new");
        var oldMods = Path.Combine(_root, "mods-old");
        var newMods = Path.Combine(_root, "mods-new");
        var oldServerPath = CreateServer(oldServers, "Alpha", "same");
        var newServerPath = CreateServer(newServers, "Alpha", "same");
        var oldModPath = CreateMod(oldMods, "12345", "same");
        var newModPath = CreateMod(newMods, "12345", "same");
        var server = new ServerInstance { Name = "Alpha", InstallPath = oldServerPath };
        var mod = new SteamMod { Name = "Existing Mod", WorkshopId = 12345, LocalPath = oldModPath };
        _db.ServerInstances.Add(server);
        _db.Mods.Add(mod);
        await _db.SaveChangesAsync();

        await _sut.ApplyStorageChangesAsync(new StorageApplyRequest(
            newMods,
            newServers,
            [
                new(StorageCandidateKind.Server, newServerPath, StorageResolutionAction.UseDiscovered, server.Id),
                new(StorageCandidateKind.Mod, newModPath, StorageResolutionAction.UseDiscovered, mod.Id)
            ]));

        Assert.Equal(Path.GetFullPath(newServerPath), _db.ServerInstances.Single().InstallPath);
        Assert.Equal(Path.GetFullPath(newModPath), _db.Mods.Single().LocalPath);
    }

    [Fact]
    public async Task MigrateStorage_CopiesThenSwitchesAndKeepsOldFiles()
    {
        var oldServers = Path.Combine(_root, "servers-old");
        var oldMods = Path.Combine(_root, "mods-old");
        var newServers = Path.Combine(_root, "servers-new");
        var newMods = Path.Combine(_root, "mods-new");
        var oldServerPath = CreateServer(oldServers, "Alpha", "same");
        var oldModPath = CreateMod(oldMods, "12345", "same");
        _db.ServerInstances.Add(new ServerInstance { Name = "Alpha", InstallPath = oldServerPath });
        _db.Mods.Add(new SteamMod { Name = "Existing Mod", WorkshopId = 12345, LocalPath = oldModPath });
        await _db.SaveChangesAsync();

        var result = await _sut.MigrateStorageAsync(new StorageMigrationRequest(newMods, newServers));

        Assert.Equal(1, result.ServersMigrated);
        Assert.Equal(1, result.ModsMigrated);
        Assert.Empty(result.Skipped);
        Assert.True(File.Exists(Path.Combine(oldServerPath, "arma3server_x64.exe")));
        Assert.True(File.Exists(Path.Combine(oldModPath, "mod.cpp")));
        Assert.StartsWith(Path.GetFullPath(newServers), _db.ServerInstances.Single().InstallPath);
        Assert.StartsWith(Path.GetFullPath(newMods), _db.Mods.Single().LocalPath);
    }

    [Fact]
    public async Task MigrateStorage_ExistingDestinationIsSkipped()
    {
        var oldServers = Path.Combine(_root, "servers-old");
        var oldServerPath = CreateServer(oldServers, "Alpha", "same");
        var newServers = Path.Combine(_root, "servers-new");
        CreateServer(newServers, "Alpha", "existing");
        _db.ServerInstances.Add(new ServerInstance { Name = "Alpha", InstallPath = oldServerPath });
        await _db.SaveChangesAsync();

        var result = await _sut.MigrateStorageAsync(new StorageMigrationRequest(Path.Combine(_root, "mods-new"), newServers));

        Assert.Equal(0, result.ServersMigrated);
        Assert.Contains(result.Skipped, s => s.Contains("destination already exists", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(oldServerPath, _db.ServerInstances.Single().InstallPath);
    }

    // Fingerprints include last-write time, as a faithful copy preserves it.
    // Pin one so two independently written fixtures look like copies.
    private static readonly DateTime FixtureWriteTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string CreateServer(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        WriteFixture(Path.Combine(path, "arma3server_x64.exe"), "bin");
        WriteFixture(Path.Combine(path, "server.cfg"), content);
        return Path.GetFullPath(path);
    }

    private static string CreateMod(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        WriteFixture(Path.Combine(path, "mod.cpp"), content);
        return Path.GetFullPath(path);
    }

    private static void WriteFixture(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, FixtureWriteTime);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "KAST.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
