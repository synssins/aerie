namespace Kb.Plugins.Actions;

/// <summary>The spaces the caller has a role in, by name, each with that role.</summary>
[Action("space.list", Read = true)]
public sealed record ListSpaces : IAction<IReadOnlyList<SpaceDto>>;

[Action("space.read", Read = true)]
public sealed record ReadSpace : IAction<SpaceDto>
{
    public required Guid SpaceId { get; init; }
}

/// <summary>Creates a space. An Admin's.</summary>
[Action("space.create")]
public sealed record CreateSpace : IAction<SpaceDto>
{
    public required string Name { get; init; }
    /// <summary>Null or blank derives a key from the name.</summary>
    public string? Key { get; init; }
    public string? Description { get; init; }
    public bool IsSensitive { get; init; }
    public bool IsPublic { get; init; }
}

/// <summary>
/// Changes a space, as one who manages it. Null leaves a value as it is.
/// The name, the sensitive flag and the public flag are an Admin's to change.
/// </summary>
[Action("space.update")]
public sealed record UpdateSpace : IAction<SpaceDto>
{
    public required Guid SpaceId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool? IsSensitive { get; init; }
    public bool? IsPublic { get; init; }
    public bool? CommentsEnabled { get; init; }
    public int? AgingDays { get; init; }
    public AgingAudience? AgingAudience { get; init; }
    public int? RecycleDays { get; init; }
}

/// <summary>Changes a space as an Admin, who needs no role in it. Null leaves a value as it is.</summary>
[Action("space.update.admin")]
public sealed record UpdateSpaceAdmin : IAction<SpaceDto>
{
    public required Guid SpaceId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool? IsSensitive { get; init; }
    public bool? IsPublic { get; init; }
    public bool? CommentsEnabled { get; init; }
    public int? AgingDays { get; init; }
    public AgingAudience? AgingAudience { get; init; }
    public int? RecycleDays { get; init; }
}

[Action("space.role.list", Read = true)]
public sealed record ListSpaceRoles : IAction<IReadOnlyList<RoleAssignmentDto>>
{
    public required Guid SpaceId { get; init; }
}

[Action("space.role.grant")]
public sealed record GrantSpaceRole : IAction<Unit>
{
    public required Guid SpaceId { get; init; }
    public required PrincipalType PrincipalType { get; init; }
    public required string PrincipalId { get; init; }
    public required SpaceRole Role { get; init; }
}

[Action("space.role.revoke")]
public sealed record RevokeSpaceRole : IAction<Unit>
{
    public required Guid SpaceId { get; init; }
    public required Guid AssignmentId { get; init; }
}

/// <summary>
/// Deletes a space: it goes to the recycle bin with everything in it, and an Admin can restore it. Only an Admin, and only with
/// the phrase "delete &lt;the space's key&gt;" in <see cref="Confirm"/>: the server asks for it, whatever a screen asked before.
/// </summary>
[Action("space.delete")]
public sealed record DeleteSpace : IAction<Unit>
{
    public required Guid SpaceId { get; init; }
    public string? Confirm { get; init; }
}

/// <summary>Takes a deleted space out of the recycle bin, as it was. Only an Admin.</summary>
[Action("space.restore")]
public sealed record RestoreSpace : IAction<SpaceDto>
{
    public required Guid SpaceId { get; init; }
}

/// <summary>The deleted spaces, for an Admin.</summary>
[Action("space.list.deleted", Read = true)]
public sealed record ListDeletedSpaces : IAction<IReadOnlyList<DeletedSpaceDto>>;

public sealed record DeletedSpaceDto(Guid Id, string Key, string Name, bool IsSensitive, DateTimeOffset DeletedAt, string DeletedBy, int Pages);

/// <summary>Every space, by name, for an Admin. No role is named.</summary>
[Action("space.list.all", Read = true)]
public sealed record ListAllSpaces : IAction<IReadOnlyList<SpaceDto>>;

public sealed record SpaceDto(Guid Id, string Key, string Name, string? Description, bool IsSensitive, bool IsPublic, string? MyRole,
    bool CommentsEnabled, int AgingDays, string AgingAudience, int? RecycleDays, DateTimeOffset CreatedAt);
public sealed record RoleAssignmentDto(Guid Id, PrincipalType PrincipalType, string PrincipalId, SpaceRole Role);
