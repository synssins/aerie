using Kb.Plugins.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Plugin.Sample;

// #region guide:data
public sealed class Ack
{
    public int Id { get; set; }
    /// <summary>The id of a core page, held as a value. A plugin table has no foreign key to a core table.</summary>
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>The plugin's table. In the database it is named plg_kb_sample__acks: the host adds the plugin's prefix.</summary>
public sealed class SampleData(PluginDbOptions options) : PluginDbContext(options)
{
    public DbSet<Ack> Acks => Set<Ack>();

    protected override void Build(ModelBuilder model)
    {
        var ack = model.Entity<Ack>().ToTable("acks");
        ack.HasIndex(a => new { a.PageId, a.UserId }).IsUnique();
        ack.Property(a => a.Name).HasMaxLength(200);
        ack.Property(a => a.Version).HasMaxLength(24);
    }
}
// #endregion
