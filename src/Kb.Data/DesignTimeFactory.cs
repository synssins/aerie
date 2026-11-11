using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kb.Data;

/// <summary>
/// Used only by `dotnet ef`. Migrations target SQL Server (production); SQLite dev/test databases use EnsureCreated.
/// The connection string is never opened when generating migrations.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<KbDbContext>
{
    public KbDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KbDbContext>()
            .UseSqlServer("Server=.\\SQLEXPRESS;Database=Kb;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True")
            .Options);
}
