namespace Kb.Core.Import;

/// <summary>
/// New: just uploaded. Working: being read and grouped, or the AI is drafting. Ready: a draft waits in the importer's inbox.
/// Failed and Blocked say why. Accepted: it became one or more pages. Discarded: the importer threw it away (its files purged).
/// Rejected: the importer set the draft aside; its files stay, and it can be drafted again.
/// </summary>
public enum ImportStatus { Queued, Working, Ready, Failed, Blocked, Accepted, Discarded, Rejected }

/// <summary>One upload: the files someone dropped at once. Grouped once read; each group becomes an item.</summary>
public sealed class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Where the importer wants the pages (a space, and a folder in it); null keeps them in the import inbox.</summary>
    public Guid? TargetSpaceId { get; set; }
    public Guid? TargetParentId { get; set; }
    /// <summary>When the importer finished uploading; until then the batch is still receiving files and is not read.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>True once its files are read and grouped into items.</summary>
    public bool Grouped { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A file of an import: one uploaded, one found inside another (a zip entry, a mail attachment) or a picture inside a document.
/// The original stays in storage until the import is accepted or discarded, for provenance.
/// </summary>
public sealed class ImportFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public Guid UserId { get; set; }
    public Guid? ItemId { get; set; }
    /// <summary>The file it came out of, if any.</summary>
    public Guid? ParentId { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    /// <summary>Text, Image, Container, Blocked or Unsupported.</summary>
    public required string Kind { get; set; }
    public string? Mime { get; set; }
    public string? Note { get; set; }
    /// <summary>The text read from it, as Markdown-like lines.</summary>
    public string? Text { get; set; }
    /// <summary>True for a picture found inside a document (it belongs to that document, not attached on its own).</summary>
    public bool Embedded { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A draft in the importer's inbox: its files, and once the AI has read them, the proposed page(s) and suggestions.</summary>
public sealed class ImportItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public Guid UserId { get; set; }
    public ImportStatus Status { get; set; }
    public required string Title { get; set; }
    /// <summary>The proposal as JSON (pages, metadata, findings, duplicates). Only the importer sees it.</summary>
    public string? ProposalJson { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>The first page made from it, once accepted.</summary>
    public Guid? PageId { get; set; }
}
