using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Plugins;
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

/// <summary>Uploaded branding image (company logo, light or dark).</summary>
public sealed class BrandAsset
{
    public required string Variant { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An image pasted or uploaded into a page in a space. The bytes live in the file store under <see cref="Id"/>.</summary>
public sealed class SpaceImage
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>True until a saved page or draft uses the image. Uploads that stay pending are cleaned up.</summary>
    public bool Pending { get; set; }
}

/// <summary>Lets a second space show an image, after a page that uses it moved there.</summary>
public sealed class SpaceImageGrant
{
    public Guid ImageId { get; set; }
    public Guid SpaceId { get; set; }
}

public sealed class KbDbContext(DbContextOptions<KbDbContext> options)
    : IdentityDbContext<KbUser, KbRole, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>Where SQLite's migrations are. SQL Server's are in this assembly; a model change needs a migration in both.</summary>
    public const string SqliteMigrations = "Kb.Data.Sqlite";

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
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<PageVersion> PageVersions => Set<PageVersion>();
    public DbSet<PageTag> PageTags => Set<PageTag>();
    public DbSet<BrandAsset> BrandAssets => Set<BrandAsset>();
    public DbSet<SpaceImage> SpaceImages => Set<SpaceImage>();
    public DbSet<SpaceImageGrant> SpaceImageGrants => Set<SpaceImageGrant>();
    public DbSet<PageDraft> PageDrafts => Set<PageDraft>();
    public DbSet<SearchTerm> SearchTerms => Set<SearchTerm>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<StoragePlace> StoragePlaces => Set<StoragePlace>();
    public DbSet<StorageMove> StorageMoves => Set<StorageMove>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<LogTarget> LogTargets => Set<LogTarget>();
    public DbSet<PluginPackage> PluginPackages => Set<PluginPackage>();
    public DbSet<PluginStatus> PluginStatuses => Set<PluginStatus>();
    public DbSet<PluginSettingValue> PluginSettingValues => Set<PluginSettingValue>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<PluginPermissionRole> PluginPermissionRoles => Set<PluginPermissionRole>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<KbUser>().HasIndex(u => u.ExternalId);
        builder.Entity<KbGroup>().HasIndex(g => g.Key).IsUnique();
        builder.Entity<KbGroupMember>().HasKey(m => new { m.GroupId, m.UserId });
        builder.Entity<KbGroupMember>().HasIndex(m => m.UserId);
        builder.Entity<Space>().HasIndex(s => s.Key).IsUnique();
        // A deleted space is not read by any query that does not ask for deleted ones. Every role, list and page check starts from
        // the space, so that is what makes a deleted space, and all in it, out of reach.
        builder.Entity<Space>().HasQueryFilter(s => s.DeletedAt == null);
        builder.Entity<SpaceRoleAssignment>().HasIndex(a => new { a.SpaceId, a.PrincipalType, a.PrincipalId }).IsUnique();
        builder.Entity<SpaceRoleAssignment>().HasIndex(a => a.PrincipalId);
        builder.Entity<SpaceAccessGrant>().HasIndex(g => new { g.UserId, g.SpaceId });
        builder.Entity<AccessRequest>().HasIndex(r => new { r.SpaceId, r.Status });
        builder.Entity<Notification>().HasIndex(n => new { n.UserId, n.ReadAt });
        builder.Entity<AuditEntry>().HasKey(a => a.Sequence);
        builder.Entity<AuditEntry>().Property(a => a.Sequence).ValueGeneratedNever();
        builder.Entity<RuntimeSetting>().HasKey(s => s.Key);
        builder.Entity<Page>().Ignore(p => p.Version);
        builder.Entity<Page>().Property(p => p.Title).HasMaxLength(200);
        builder.Entity<Page>().HasIndex(p => new { p.SpaceId, p.ParentId });
        builder.Entity<PageVersion>().Property(v => v.Title).HasMaxLength(200);
        builder.Entity<PageVersion>().HasIndex(v => new { v.PageId, v.Major, v.Minor }).IsUnique();
        builder.Entity<PageTag>().HasKey(t => new { t.PageId, t.Tag });
        builder.Entity<BrandAsset>().HasKey(a => a.Variant);
        builder.Entity<BrandAsset>().Property(a => a.Variant).HasMaxLength(16);
        builder.Entity<PageTag>().Property(t => t.Tag).HasMaxLength(50);
        builder.Entity<PageTag>().HasIndex(t => t.Tag);
        builder.Entity<SpaceImage>().Property(i => i.FileName).HasMaxLength(255);
        builder.Entity<SpaceImage>().Property(i => i.ContentType).HasMaxLength(64);
        builder.Entity<SpaceImage>().HasIndex(i => i.SpaceId);
        builder.Entity<SpaceImageGrant>().HasKey(g => new { g.ImageId, g.SpaceId });
        // Recycle bin: deleted items drop out of every query unless it asks for them (IgnoreQueryFilters).
        builder.Entity<Page>().HasQueryFilter(p => p.DeletedAt == null);
        builder.Entity<Page>().HasIndex(p => p.DeletedAt);
        builder.Entity<PageDraft>().HasKey(d => d.PageId);
        builder.Entity<PageDraft>().Property(d => d.Title).HasMaxLength(200);
        builder.Entity<PageDraft>().Property(d => d.BaseVersion).HasMaxLength(24);
        builder.Entity<SearchTerm>().HasKey(t => new { t.PageId, t.Term });
        builder.Entity<SearchTerm>().Property(t => t.Term).HasMaxLength(40);
        builder.Entity<SearchTerm>().HasIndex(t => t.Term);
        builder.Entity<Attachment>().Property(a => a.FileName).HasMaxLength(255);
        builder.Entity<Attachment>().HasIndex(a => a.PageId);
        builder.Entity<Announcement>().Property(a => a.Title).HasMaxLength(120);
        builder.Entity<Announcement>().Property(a => a.Body).HasMaxLength(2000);
        builder.Entity<StoragePlace>().Property(p => p.Name).HasMaxLength(100);
        builder.Entity<StoragePlace>().Property(p => p.Kind).HasMaxLength(40);
        builder.Entity<StorageMove>().Property(m => m.LastError).HasMaxLength(500);
        builder.Entity<StorageMove>().Property(m => m.ClaimedBy).HasMaxLength(100);
        builder.Entity<PluginPackage>().HasKey(p => new { p.PluginId, p.Version });
        builder.Entity<PluginPackage>().Property(p => p.PluginId).HasMaxLength(64);
        builder.Entity<PluginPackage>().Property(p => p.Version).HasMaxLength(32);
        builder.Entity<PluginPackage>().Property(p => p.Sha256).HasMaxLength(64);
        builder.Entity<PluginPackage>().Property(p => p.Source).HasMaxLength(400);
        builder.Entity<PluginStatus>().HasKey(s => s.PluginId);
        builder.Entity<PluginStatus>().Property(s => s.PluginId).HasMaxLength(64);
        builder.Entity<PluginStatus>().Property(s => s.ActiveVersion).HasMaxLength(32);
        builder.Entity<PluginStatus>().Property(s => s.Reason).HasMaxLength(300);
        builder.Entity<PluginSettingValue>().HasKey(v => new { v.PluginId, v.Key });
        builder.Entity<PluginSettingValue>().Property(v => v.PluginId).HasMaxLength(64);
        builder.Entity<PluginSettingValue>().Property(v => v.Key).HasMaxLength(100);
        builder.Entity<PluginPermissionRole>().HasKey(p => p.Permission);
        builder.Entity<PluginPermissionRole>().Property(p => p.Permission).HasMaxLength(200);
        builder.Entity<UserPermission>().HasKey(p => new { p.UserId, p.Permission });
        builder.Entity<UserPermission>().Property(p => p.Permission).HasMaxLength(200);
        builder.Entity<JobRun>().HasKey(r => new { r.PluginId, r.Name });
        builder.Entity<JobRun>().Property(r => r.PluginId).HasMaxLength(64);
        builder.Entity<JobRun>().Property(r => r.Name).HasMaxLength(100);
        builder.Entity<JobRun>().Property(r => r.LastError).HasMaxLength(500);
        builder.Entity<JobRun>().Property(r => r.ClaimedBy).HasMaxLength(100);
        builder.Entity<LogTarget>().Property(t => t.Name).HasMaxLength(100);
        builder.Entity<LogTarget>().Property(t => t.Kind).HasMaxLength(40);
        builder.Entity<LogTarget>().Property(t => t.LastError).HasMaxLength(500);
        builder.Entity<LogTarget>().Property(t => t.ClaimedBy).HasMaxLength(100);
        builder.Entity<ApiToken>().Property(t => t.Name).HasMaxLength(100);
        builder.Entity<ApiToken>().Property(t => t.Prefix).HasMaxLength(16);
        builder.Entity<ApiToken>().Property(t => t.Hash).HasMaxLength(32);
        builder.Entity<ApiToken>().HasIndex(t => t.Hash).IsUnique();
        builder.Entity<ApiToken>().HasIndex(t => t.OwnerUserId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare DateTimeOffset natively; store as binary ticks there. SQL Server keeps datetimeoffset.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
    }
}
