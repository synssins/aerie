using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kb.Data.Sqlite;

/// <summary>Used only by `dotnet ef` to make the SQLite migrations. The file is never opened when generating them.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<KbDbContext>
{
    public KbDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KbDbContext>().UseSqlite("Data Source=design.db", s => s.MigrationsAssembly(KbDbContext.SqliteMigrations)).Options);
}
