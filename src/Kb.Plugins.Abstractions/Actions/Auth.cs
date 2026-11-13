using System.Text.Json.Serialization;

namespace Kb.Plugins.Actions;

/// <summary>Signs a person in with a local user name and password. Sent by nobody: the caller is not signed in yet.</summary>
// A member under [JsonIgnore] cannot be "required": System.Text.Json refuses to serialize such a type.
[Action("auth.signin")]
public sealed record SignIn : IAction<MeResponse>
{
    public required string UserName { get; init; }
    [JsonIgnore] public string Password { get; init; } = "";

    /// <summary>A record prints every member; the password stays out, so a log line of the action cannot hold it.</summary>
    public override string ToString() => $"SignIn {{ UserName = {UserName} }}";
}

[Action("auth.signout")]
public sealed record SignOut : IAction<Unit>;

/// <summary>The caller's own record.</summary>
[Action("me.read", Read = true)]
public sealed record ReadMe : IAction<MeResponse>;

[Action("me.settings.read", Read = true)]
public sealed record ReadMySettings : IAction<UserSettings>;

[Action("me.settings.save")]
public sealed record SaveMySettings : IAction<UserSettings>
{
    public required string Theme { get; init; }
    public required string Width { get; init; }
    /// <summary>"last" or a space id.</summary>
    public required string StartPage { get; init; }
    public required string CodeLanguage { get; init; }
    public Dictionary<string, NotifyPref>? Notify { get; init; }
}

/// <summary>Whether the first admin still has to be made, and whether making one needs the setup code.</summary>
[Action("setup.status", Read = true)]
public sealed record ReadSetupStatus : IAction<SetupStatus>;

/// <summary>First-run setup: makes the first regular admin and signs that person in.</summary>
[Action("setup.run")]
public sealed record RunSetup : IAction<MeResponse>
{
    public required string UserName { get; init; }
    public required string DisplayName { get; init; }
    [JsonIgnore] public string Password { get; init; } = "";
    [JsonIgnore] public string? Code { get; init; }

    public override string ToString() => $"RunSetup {{ UserName = {UserName}, DisplayName = {DisplayName} }}";
}

public sealed record MeResponse(Guid Id, string UserName, string DisplayName, bool IsAdmin, bool IsBreakGlass, bool CanAnnounce = false);
public sealed record SetupStatus(bool Required, bool CodeRequired);

/// <summary>Per-user app settings. Stored as JSON on the user.</summary>
public sealed record UserSettings(string Theme, string Width, string StartPage, string CodeLanguage, Dictionary<string, NotifyPref> Notify)
{
    public static UserSettings Default => new("system", "boxed", "last", "plaintext", []);
}
