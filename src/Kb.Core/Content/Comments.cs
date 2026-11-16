namespace Kb.Core.Content;

public enum CommentState { Visible, Hidden, Deleted }

/// <summary>
/// A comment on a page, or a reply to one (one level only). Plain text, never markup (contracts/behaviour/comments.md; questions Q-0022, Q-0023).
/// A comment its author deleted keeps its row only while it has replies, as a placeholder without text.
/// </summary>
public sealed class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PageId { get; set; }
    /// <summary>The top-level comment this replies to; null for a top-level comment.</summary>
    public Guid? ParentId { get; set; }
    public Guid AuthorId { get; set; }
    /// <summary>Null once the author deleted it: the text is gone for good.</summary>
    public string? Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public CommentState State { get; set; }
    /// <summary>Its author deleted it: it cannot be restored, not even by moderators.</summary>
    public bool DeletedByAuthor { get; set; }
    /// <summary>When a moderator last hid or deleted it, and who; a moderator's delete is purged 30 days later.</summary>
    public DateTimeOffset? ModeratedAt { get; set; }
    public Guid? ModeratedBy { get; set; }
}
