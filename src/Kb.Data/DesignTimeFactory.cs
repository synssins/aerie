using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kb.Data;

/// <summary>
/// Used only by `dotnet ef`. These are the SQL Server migrations; SQLite's are in Kb.Data.Sqlite. A model change needs a migration in both.
/// The connection string is never opened when generating migrations.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<KbDbContext>
{
    public KbDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KbDbContext>()
            .UseSqlServer("Server=.\\SQLEXPRESS;Database=Kb;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True")
            .Options);
}
