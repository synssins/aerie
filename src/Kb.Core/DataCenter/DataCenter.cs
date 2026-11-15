namespace Kb.Core.DataCenter;

/// <summary>
/// What the Data Center compatible surface needs to know of a space that aerie itself does not hold (contracts/behaviour/common.md, Identifiers;
/// spaces.md). One row per space, made when the surface first meets the space. <see cref="Id"/> is the space's compatible id: it comes from
/// the database's own sequence, so it is never reused.
/// </summary>
public sealed class DcSpace
{
    public long Id { get; set; }
    public Guid SpaceId { get; set; }
    /// <summary>Who made the space through the surface; null for one made elsewhere.</summary>
    public Guid? CreatedBy { get; set; }
    /// <summary>Set for a personal space: whose it is.</summary>
    public Guid? PersonalOwnerId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>Work started through the surface that clients follow as a long task (common.md, Long tasks).</summary>
public sealed class DcLongTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Who started it.</summary>
    public Guid? UserId { get; set; }
    /// <summary>What it is, such as "space.delete".</summary>
    public required string Name { get; set; }
    /// <summary>What it works on, such as the space key.</summary>
    public string Subject { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Successful { get; set; }
}
