using Kb.Plugins;

namespace Kb.Core.Inventory;

/// <summary>An application in the inventory. Owner: the person or group who owns its escalation path (as on a page).</summary>
public sealed class InvApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// <summary>The name in lower case: names are unique whatever their case.</summary>
    public required string NameKey { get; set; }
    public string? Vendor { get; set; }
    public PrincipalType? OwnerType { get; set; }
    /// <summary>A user id, or a group's key, as in a space role assignment.</summary>
    public string? OwnerId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One application on one server, at a version. One per application and server.</summary>
public sealed class InvInstall
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public required string Server { get; set; }
    /// <summary>The server's name in lower case: "ERP01" and "erp01" are one server.</summary>
    public required string ServerKey { get; set; }
    public required string Version { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    /// <summary>Who or what made the last change: a person's name, "CSV import (name)", or "Sync: source (by)".</summary>
    public required string ChangedBy { get; set; }
}

public enum InvChange { Added, Changed, Removed }

/// <summary>Every change of an install, kept after the install itself is removed.</summary>
public sealed class InvHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public required string Server { get; set; }
    public required string ServerKey { get; set; }
    public InvChange Kind { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public DateTimeOffset At { get; set; }
    public required string By { get; set; }
}

/// <summary>Token: one API token (a service token has no person behind it), by its id.</summary>
public enum InvRuleKind { Everyone, Space, Role, User, Group, Token }

/// <summary>
/// Who views (Edit false) or edits (Edit true) the inventory. TargetId: the space id, the role id, the user id, the group key or the token id.
/// Editing includes viewing. Admins always do both.
/// </summary>
public sealed class InvAccessRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Edit { get; set; }
    public InvRuleKind Kind { get; set; }
    public string? TargetId { get; set; }
}

/// <summary>A page applies to an application, on some of its servers or (none named) all of them.</summary>
public sealed class PageAppLink
{
    public Guid PageId { get; set; }
    public Guid ApplicationId { get; set; }
    /// <summary>JSON array of server names.</summary>
    public string ServersJson { get; set; } = "[]";
}
