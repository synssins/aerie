namespace Kb.Plugins.Actions;

/// <summary>The home page: the announcements that are up, and the pages changed last in the caller's spaces.</summary>
[Action("home.read", Read = true)]
public sealed record ReadHome : IAction<HomeDto>;

[Action("announcement.post")]
public sealed record PostAnnouncement : IAction<AnnouncementPostedDto>
{
    public required string Title { get; init; }
    public string? Body { get; init; }
    /// <summary>Null: it stays up until someone removes it.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

[Action("announcement.remove")]
public sealed record RemoveAnnouncement : IAction<Unit>
{
    public required Guid AnnouncementId { get; init; }
}

/// <summary>Says whether a user may post and remove announcements.</summary>
[Action("user.announcer.set")]
public sealed record SetAnnouncer : IAction<Unit>
{
    public required Guid UserId { get; init; }
    public required bool Enabled { get; init; }
}

public sealed record AnnouncementDto(Guid Id, string Title, string Body, string By, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record RecentPageDto(Guid Id, string Title, Guid SpaceId, string SpaceName, DateTimeOffset UpdatedAt, string UpdatedBy);
public sealed record HomeDto(IReadOnlyList<AnnouncementDto> Announcements, IReadOnlyList<RecentPageDto> Recent);
public sealed record AnnouncementPostedDto(Guid Id);
