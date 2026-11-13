namespace Kb.Plugins.Actions;

/// <summary>Asks the Owners of a space for a role in it.</summary>
[Action("access.request")]
public sealed record RequestAccess : IAction<AccessRequestedDto>
{
    public required Guid SpaceId { get; init; }
    public required SpaceRole Role { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Approves or denies an access request.</summary>
[Action("access.decide")]
public sealed record DecideAccess : IAction<Unit>
{
    public required Guid RequestId { get; init; }
    public required bool Approve { get; init; }
}

/// <summary>An Admin opens a sensitive space for a limited time.</summary>
[Action("space.sensitive.emergency")]
public sealed record OpenEmergencyAccess : IAction<EmergencyAccessDto>
{
    public required Guid SpaceId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>The caller's notifications, newest first, at most 100.</summary>
[Action("notification.list", Read = true)]
public sealed record ListNotifications : IAction<IReadOnlyList<NotificationDto>>
{
    public bool UnreadOnly { get; init; }
}

[Action("notification.count", Read = true)]
public sealed record CountNotifications : IAction<NotificationCountDto>;

[Action("notification.mark")]
public sealed record MarkNotification : IAction<Unit>
{
    public required Guid NotificationId { get; init; }
}

[Action("notification.mark.all")]
public sealed record MarkAllNotifications : IAction<Unit>;

public sealed record AccessRequestedDto(Guid Id);
public sealed record EmergencyAccessDto(DateTimeOffset? ExpiresAt);
public sealed record NotificationDto(Guid Id, Guid UserId, string Kind, string Title, string? Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record NotificationCountDto(int Unread);
