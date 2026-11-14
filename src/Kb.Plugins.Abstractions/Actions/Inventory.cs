namespace Kb.Plugins.Actions;

/// <summary>
/// Brings the application inventory up to date from a list of installs: what a CMDB or scan plugin, a script with a write token,
/// and the CSV import send. New applications and installs are added, changed versions recorded, and (RemoveMissing) installs on the
/// servers in the list that the list no longer names are removed. Sending the same list twice changes nothing.
/// Apply false answers what would change and changes nothing.
/// </summary>
[Action("inventory.installs.sync")]
public sealed record SyncInstalls : IAction<SyncResultDto>
{
    /// <summary>Who the data comes from ("cmdb", a file name): the history names it.</summary>
    public required string Source { get; init; }
    public required IReadOnlyList<SyncRowDto> Rows { get; init; }
    public bool RemoveMissing { get; init; }
    public bool Apply { get; init; } = true;
}

/// <summary>Vendor: only set on a new application, or on one that has none.</summary>
public sealed record SyncRowDto(string Application, string? Vendor, string Server, string Version);

/// <summary>Kind: Added, Changed, Unchanged or Removed. From and To are the versions.</summary>
public sealed record SyncChangeDto(string Application, string Server, string Kind, string? From, string? To);

public sealed record SyncResultDto(int Added, int Changed, int Unchanged, int Removed, int NewApplications, IReadOnlyList<SyncChangeDto> Changes);

/// <summary>A page's details: the applications (and servers) it applies to, its owner, and how often it is reviewed.</summary>
public sealed record PageDetailsDto(IReadOnlyList<AppliesToDto> AppliesTo, OwnerRefDto? Owner, int? ReviewMonths);

/// <summary>Servers: some of the application's servers; none means all of them.</summary>
public sealed record AppliesToDto(Guid ApplicationId, string Name, IReadOnlyList<string> Servers);

/// <summary>Type: User or Group. Id: the user id or the group's key.</summary>
public sealed record OwnerRefDto(string Type, string Id, string Name);
