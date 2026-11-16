namespace Kb.Core.Content;

/// <summary>
/// A person's rating of a page: one per person per page (contracts/behaviour/ratings.md; question Q-0028). Negative ratings and their reasons
/// are seen only by the person who gave them and the space's Supervisors and Owners.
/// </summary>
public sealed class Rating
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public bool Positive { get; set; }
    /// <summary>"This solved my problem"; only with a positive rating.</summary>
    public bool Solved { get; set; }
    /// <summary>Only with a negative rating, at most 280 characters.</summary>
    public string? Reason { get; set; }
    /// <summary>The page version the person was reading when they last saved the rating.</summary>
    public string Version { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
