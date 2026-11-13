using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;

namespace Kb.Web;

/// <summary>Per-user app settings. Stored as JSON on the user.</summary>
public sealed record UserSettings(string Theme, string Width, string StartPage, string CodeLanguage, Dictionary<string, NotifyPref> Notify)
{
    public static UserSettings Default => new("system", "boxed", "last", "plaintext", []);
}

public static partial class UserSettingsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Themes = ["light", "dark", "system"];
    private static readonly string[] Widths = ["boxed", "full"];

    public static UserSettings Read(KbUser u)
    {
        try
        {
            var s = JsonSerializer.Deserialize<UserSettings>(u.SettingsJson, Json);
            return s is null ? UserSettings.Default : s with { Notify = s.Notify ?? [] };
        }
        catch (JsonException) { return UserSettings.Default; }
    }
}
