using KAST.Core.Models;
using KAST.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KAST.Tests;

/// <summary>
/// Verifies TimeOnly schedule columns round-trip on SQLite and that legacy
/// "HH:mm" text values (written before the TimeOnly migration) still read.
/// </summary>
public class ServerInstanceTimeOnlySqliteTests
{
    [Fact]
    public async Task TimeOnlyScheduleValues_RoundTrip()
    {
        var (db, connection) = CreateDb();
        try
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Name = "Scheduled",
                InstallPath = "/tmp/a3",
                AutoStartTime = new TimeOnly(8, 30),
                AutoStopTime = new TimeOnly(23, 0)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var loaded = await db.ServerInstances.AsNoTracking().SingleAsync();

            Assert.Equal(new TimeOnly(8, 30), loaded.AutoStartTime);
            Assert.Equal(new TimeOnly(23, 0), loaded.AutoStopTime);
        }
        finally
        {
            db.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task LegacyHhmmTextValues_StillReadable()
    {
        var (db, connection) = CreateDb();
        try
        {
            // Insert a row via EF, then rewrite the schedule columns to legacy
            // "HH:mm" text the way pre-TimeOnly versions stored them.
            db.ServerInstances.Add(new ServerInstance
            {
                Name = "Legacy",
                InstallPath = "/tmp/a3",
                AutoStartTime = new TimeOnly(1, 1),
                AutoStopTime = new TimeOnly(1, 1)
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"ServerInstances\" SET \"AutoStartTime\" = '08:30', \"AutoStopTime\" = '23:00'");
            db.ChangeTracker.Clear();

            var loaded = await db.ServerInstances.AsNoTracking().SingleAsync();

            Assert.Equal(new TimeOnly(8, 30), loaded.AutoStartTime);
            Assert.Equal(new TimeOnly(23, 0), loaded.AutoStopTime);
        }
        finally
        {
            db.Dispose();
            connection.Dispose();
        }
    }

    private static (KastDbContext Db, Microsoft.Data.Sqlite.SqliteConnection Connection) CreateDb()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<KastDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new KastDbContext(options);
        db.Database.EnsureCreated();
        return (db, connection);
    }
}
