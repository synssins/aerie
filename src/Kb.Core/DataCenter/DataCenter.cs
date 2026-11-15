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

/// <summary>
/// The compatible id of a piece of content: a page or folder, or an attachment (common.md, Identifiers). All content shares this one sequence,
/// so a page and an attachment never have the same id.
/// </summary>
public sealed class DcContent
{
    public long Id { get; set; }
    /// <summary>The aerie id of the page, folder or attachment.</summary>
    public Guid AerieId { get; set; }
    /// <summary>"page" or "attachment".</summary>
    public required string Kind { get; set; }
}

/// <summary>
/// The compatible number of one version of a page: 1, 2, 3 in the order aerie made them. A number is never shifted or reused; a version
/// removed through the surface keeps its row, marked removed, and aerie keeps the version itself (its history is append-only).
/// </summary>
public sealed class DcVersion
{
    public long PageVersionId { get; set; }
    public Guid PageId { get; set; }
    public int Number { get; set; }
    public bool Removed { get; set; }
}

/// <summary>The compatible id of a label: one per prefix and name on the site (labels-properties.md).</summary>
public sealed class DcLabel
{
    public long Id { get; set; }
    public required string Prefix { get; set; }
    public required string Name { get; set; }
}

/// <summary>
/// One version of an attachment's file (attachments.md). Aerie's own attachment row always holds the current file under its own id, so aerie's
/// download keeps serving it; an older version's bytes are kept under <see cref="BlobId"/>. A version removed through the surface keeps its row,
/// marked removed, and its file (aerie keeps the files of removed attachments too).
/// </summary>
public sealed class DcAttachmentVersion
{
    public long Id { get; set; }
    public Guid AttachmentId { get; set; }
    public int Number { get; set; }
    /// <summary>The file store key of this version's bytes; the attachment's own id for the current version.</summary>
    public Guid BlobId { get; set; }
    public long Size { get; set; }
    public required string MediaType { get; set; }
    public string Comment { get; set; } = "";
    public bool MinorEdit { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Removed { get; set; }
}
