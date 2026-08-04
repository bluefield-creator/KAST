using KAST.Infrastructure.Data;
using KAST.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KAST.Tests;

/// <summary>
/// Regression tests for ApiKeyService against a real SQLite database.
/// The InMemory provider evaluates query predicates client-side, so it does
/// not catch EF translation errors — ValidateKeyAsync previously passed
/// the non-translatable VerifyKey method as a query predicate and always
/// threw InvalidOperationException on SQLite.
/// </summary>
public class ApiKeyServiceSqliteTests
{
    [Fact]
    public async Task ValidateKey_ValidKey_ReturnsTrueOnSqlite()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"kast-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<KastDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .Options;

        try
        {
            await using (var setupDb = new KastDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            string rawKey;
            await using (var createDb = new KastDbContext(options))
            {
                var service = new ApiKeyService(createDb);
                (_, rawKey) = await service.CreateApiKeyAsync("SQLite Key");
            }

            await using var validateDb = new KastDbContext(options);
            var sut = new ApiKeyService(validateDb);

            var isValid = await sut.ValidateKeyAsync(rawKey);

            Assert.True(isValid);
        }
        finally
        {
            DeleteIfExists(dbPath);
            DeleteIfExists($"{dbPath}-shm");
            DeleteIfExists($"{dbPath}-wal");
        }
    }

    [Fact]
    public async Task ValidateKey_BogusKey_ReturnsFalseOnSqlite()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"kast-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<KastDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .Options;

        try
        {
            await using (var setupDb = new KastDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new KastDbContext(options);
            var sut = new ApiKeyService(db);

            var isValid = await sut.ValidateKeyAsync("kast_xx_this_is_totally_bogus_key");

            Assert.False(isValid);
        }
        finally
        {
            DeleteIfExists(dbPath);
            DeleteIfExists($"{dbPath}-shm");
            DeleteIfExists($"{dbPath}-wal");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
