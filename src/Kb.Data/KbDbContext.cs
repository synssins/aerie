using Kb.Core.Access;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Core.Spaces;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Kb.Data;

public sealed class AuditEntry
{
    public long Sequence { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Action { get; set; }
    public Guid? ActorId { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public Guid? SpaceId { get; set; }
    /// <summary>JSON object of string values.</summary>
    public required string DataJson { get; set; }
    public required string PrevHash { get; set; }
    public required string Hash { get; set; }
}

public sealed class KbDbContext(DbContextOptions<KbDbContext> options)
    : IdentityDbContext<KbUser, KbRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<KbGroup> KbGroups => Set<KbGroup>();
    public DbSet<KbGroupMember> KbGroupMembers => Set<KbGroupMember>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<SpaceRoleAssignment> SpaceRoleAssignments => Set<SpaceRoleAssignment>();
    public DbSet<SpaceAccessGrant> SpaceAccessGrants => Set<SpaceAccessGrant>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<RuntimeSetting> RuntimeSettings => Set<RuntimeSetting>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<KbUser>().HasIndex(u => u.ExternalId);
        builder.Entity<KbGroup>().HasIndex(g => g.Key).IsUnique();
        builder.Entity<KbGroupMember>().HasKey(m => new { m.GroupId, m.UserId });
        builder.Entity<KbGroupMember>().HasIndex(m => m.UserId);
        builder.Entity<Space>().HasIndex(s => s.Key).IsUnique();
        builder.Entity<SpaceRoleAssignment>().HasIndex(a => new { a.SpaceId, a.PrincipalType, a.PrincipalId }).IsUnique();
        builder.Entity<SpaceRoleAssignment>().HasIndex(a => a.PrincipalId);
        builder.Entity<SpaceAccessGrant>().HasIndex(g => new { g.UserId, g.SpaceId });
        builder.Entity<AccessRequest>().HasIndex(r => new { r.SpaceId, r.Status });
        builder.Entity<Notification>().HasIndex(n => new { n.UserId, n.ReadAt });
        builder.Entity<AuditEntry>().HasKey(a => a.Sequence);
        builder.Entity<AuditEntry>().Property(a => a.Sequence).ValueGeneratedNever();
        builder.Entity<RuntimeSetting>().HasKey(s => s.Key);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare DateTimeOffset natively; store as binary ticks there. SQL Server keeps datetimeoffset.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
    }
}
