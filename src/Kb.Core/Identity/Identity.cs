using Microsoft.AspNetCore.Identity;

namespace Kb.Core.Identity;

public enum UserSource { Local, Entra, Ldap }

public sealed class KbUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public UserSource Source { get; set; }
    /// <summary>Directory object id (Entra oid / LDAP objectGUID). Null for local accounts.</summary>
    public string? ExternalId { get; set; }
    public bool IsBreakGlass { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>May post and remove home-page announcements. Independent of Admin and space roles.</summary>
    public bool CanAnnounce { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Set when the user leaves every allowed group or is removed. Attribution greys out but stays.</summary>
    public DateTimeOffset? DeactivatedAt { get; set; }
    /// <summary>Per-user app settings (theme, layout, notifications) as JSON. See Kb.Web UserSettings.</summary>
    public string SettingsJson { get; set; } = "{}";
}

public sealed class KbRole : IdentityRole<Guid>;

/// <summary>A directory group (Entra/LDAP, keyed by its external id) or an Aerie-managed local group.</summary>
public sealed class KbGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>External object id for directory groups; "local:{Id}" for local groups. Used as the principal id in role assignments.</summary>
    public required string Key { get; set; }
    public required string Name { get; set; }
    public bool IsLocal { get; set; }
}

public sealed class KbGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class RuntimeSetting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public static class RuntimeSettingKeys
{
    public const string PublicSpaceEnabled = "PublicSpaceEnabled";
    public const string ExternalAdminVerifiedAt = "ExternalAdminVerifiedAt";
    public const string LocalSignInDisabled = "LocalSignInDisabled";
}
