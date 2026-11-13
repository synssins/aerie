using Kb.Core.Identity;
using System.Text.Json;

namespace Kb.Web;

/// <summary>How a user's settings are stored (JSON on the user) and what they may hold.</summary>
public static class UserSettingsModel
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly string[] Themes = ["light", "dark", "system"];
    internal static readonly string[] Widths = ["boxed", "full"];

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
