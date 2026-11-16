using Kb.Plugins;

namespace Kb.Core.Content;

public enum PageKind { Folder, Page }

/// <summary>A folder or page in a space's tree. Only folders hold children. Folders have no content or versions.</summary>
public sealed class Page
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SpaceId { get; set; }
    public Guid? ParentId { get; set; }
    public PageKind Kind { get; set; }
    public required string Title { get; set; }
    /// <summary>Editor document (ProseMirror JSON). Rendered by the client editor, never as raw HTML.</summary>
    public string ContentJson { get; set; } = "";
    public int Major { get; set; } = 1;
    public int Minor { get; set; }
    public int Sort { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Set while the item is in the recycle bin. Deleted items are hidden from every normal query.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    /// <summary>The item the user deleted; its whole subtree shares this id so it restores as one.</summary>
    public Guid? DeleteRootId { get; set; }
    /// <summary>"Empty bin" in a space hides the item there; the admin bin still has it.</summary>
    public bool BinHidden { get; set; }
    /// <summary>Plain text of title, tags and body, for search snippets.</summary>
    public string SearchText { get; set; } = "";
    /// <summary>Set when the page holds text addressed to an AI (what and where): AI features leave it alone until it is gone or reviewed.</summary>
    public string? AiFlag { get; set; }
    /// <summary>What an Owner or Admin marked reviewed (a hash of the findings): a page that documents prompt injection on purpose.</summary>
    public string? AiReviewed { get; set; }
    /// <summary>
    /// Who owns the process or escalation path the page is about (not who wrote it): a user id or a group's key.
    /// Example: an ERP error page whose steps end in "escalate to the ERP Support Team" is owned by that team.
    /// </summary>
    public PrincipalType? OwnerType { get; set; }
    public string? OwnerId { get; set; }
    /// <summary>How often the page is to be reviewed, in months; null for none.</summary>
    public int? ReviewMonths { get; set; }
    /// <summary>The page's switches for comments and ratings, set by the space's Supervisors and Owners (comments.md, Switching comments on and off).</summary>
    public bool CommentsEnabled { get; set; } = true;
    public bool RatingsEnabled { get; set; } = true;

    public string Version => $"{Major}.{Minor}";
}

/// <summary>Unpublished edits. One per page: whoever holds it is "editing".</summary>
public sealed class PageDraft
{
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public required string Title { get; set; }
    public required string ContentJson { get; set; }
    /// <summary>Comma separated.</summary>
    public string Tags { get; set; } = "";
    public required string BaseVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Search index row: a word on a page, weighted by where it appears.</summary>
public sealed class SearchTerm
{
    public Guid PageId { get; set; }
    public required string Term { get; set; }
    public int Weight { get; set; }
}

/// <summary>A file attached to a page. The bytes live in the file store under <see cref="Id"/>.</summary>
public sealed class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PageId { get; set; }
    public required string FileName { get; set; }
    public long Size { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>A notice on the home page.</summary>
public sealed class Announcement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public string Body { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>Every publish of a page. History is append-only.</summary>
public sealed class PageVersion
{
    public long Id { get; set; }
    public Guid PageId { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public required string Title { get; set; }
    public required string ContentJson { get; set; }
    public Guid AuthorId { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }
    /// <summary>editor, api, plugin, import.</summary>
    public string Source { get; set; } = "editor";
}

/// <summary>Freeform tag on a page. Not versioned.</summary>
public sealed class PageTag
{
    public Guid PageId { get; set; }
    public required string Tag { get; set; }
}
