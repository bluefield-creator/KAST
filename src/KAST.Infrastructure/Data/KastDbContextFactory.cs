using Microsoft.EntityFrameworkCore;

namespace KAST.Infrastructure.Data;

/// <summary>
/// Creates short-lived contexts for work that must not share the circuit- or
/// request-scoped <see cref="KastDbContext"/> (long-running lifecycle
/// operations, callbacks arriving on other threads).
/// </summary>
public sealed class KastDbContextFactory(DbContextOptions<KastDbContext> options) : IDbContextFactory<KastDbContext>
{
    public KastDbContext CreateDbContext() => new(options);
}
