using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration;

/// <summary>
/// The database is made and upgraded by migrations on both providers: SQL Server, and SQLite for an install in one file.
/// A change to the model without a migration for each of them fails here, before it can fail an upgrade.
/// </summary>
public sealed class MigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SqlServer_MigrationsMatchTheModel()
    {
        using var db = new KbDbContext(new DbContextOptionsBuilder<KbDbContext>().UseSqlServer("Server=.;Database=none").Options);
        Assert.False(db.Database.HasPendingModelChanges(), "The model changed: add a SQL Server migration (Kb.Data).");
    }

    [Fact]
    public void Sqlite_MigrationsMatchTheModel()
    {
        using var db = new KbDbContext(new DbContextOptionsBuilder<KbDbContext>()
            .UseSqlite("Data Source=:memory:", s => s.MigrationsAssembly(KbDbContext.SqliteMigrations)).Options);
        Assert.False(db.Database.HasPendingModelChanges(), "The model changed: add a SQLite migration (Kb.Data.Sqlite).");
    }

    [Fact]
    public async Task ANewSqliteFile_IsMadeByItsMigrations()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        await f.NewClient().GetAsync("/health/ready", Ct);
        await f.WithDbAsync(async db =>
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(Ct));
        });
    }
}
