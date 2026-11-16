using Microsoft.AspNetCore.Identity;

namespace Kb.Core.Identity;

/// <summary>Service: an account that only API tokens act as. It has no password and cannot sign in.</summary>
public enum UserSource { Local, Entra, Ldap, Service }

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
    /// <summary>
    /// Set when an Admin deleted the account. The row stays so that history (authors, audit entries) still names a person; the
    /// account holds nothing, cannot sign in, and its user name is free for a new account. Always deactivated as well.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>Per-user app settings (theme, layout, notifications) as JSON. See Kb.Web UserSettings.</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>The person turned on one-time codes for their own account. Admins, and everyone when the setting says so, use them anyway.</summary>
    public bool CodesRequired { get; set; }
    /// <summary>An Admin turned codes off for this account (a shared read-only demo account). Never applies to an Admin.</summary>
    public bool CodesExempt { get; set; }
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
    public const string ExternalAdminVerifiedAt = "ExternalAdminVerifiedAt";
    public const string LocalSignInDisabled = "LocalSignInDisabled";
    /// <summary>"false" turns off the password in HTTP Basic on the compatible surface for everyone; tokens in Basic still work (identity.md).</summary>
    public const string CompatBasicPasswordEnabled = "Compat.BasicPasswordEnabled";
    /// <summary>Who must use one-time codes at sign-in: "admins" (the default) or "everyone".</summary>
    public const string CodesRequiredFor = "CodesRequiredFor";
    /// <summary>Raised by every plugin change. A server that started with an older number restarts.</summary>
    public const string PluginGeneration = "Plugins.Generation";
    /// <summary>Raised by every plugin settings change. A server that sees a newer one reloads its settings.</summary>
    public const string PluginSettingsStamp = "Plugins.SettingsStamp";
}
