using System.Text.Json;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Microsoft.AspNetCore.Identity;

namespace Kb.Web;

/// <summary>Per-user app settings. Stored as JSON on the user.</summary>
public sealed record UserSettings(string Theme, string Width, string StartPage, string CodeLanguage, Dictionary<string, NotifyPref> Notify)
{
    public static UserSettings Default => new("system", "boxed", "last", "plaintext", []);
}

public static class UserSettingsEndpoints
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

    public static void MapUserSettingsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/settings", async (HttpContext http, UserManager<KbUser> users) =>
        {
            var u = await users.FindByIdAsync(http.User.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            var s = Read(u);
            // A missing or empty document means defaults.
            return Results.Ok(string.IsNullOrEmpty(s.Theme) ? UserSettings.Default : s);
        });

        api.MapPut("/me/settings", async (UserSettings req, HttpContext http, UserManager<KbUser> users) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!Themes.Contains(req.Theme)) errors["theme"] = ["light, dark or system."];
            if (!Widths.Contains(req.Width)) errors["width"] = ["boxed or full."];
            if (req.StartPage != "last" && !Guid.TryParse(req.StartPage, out _)) errors["startPage"] = ["last or a space id."];
            if (string.IsNullOrWhiteSpace(req.CodeLanguage) || req.CodeLanguage.Length > 32) errors["codeLanguage"] = ["1-32 characters."];
            if ((req.Notify?.Count ?? 0) > 50) errors["notify"] = ["Too many entries."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var u = await users.FindByIdAsync(http.User.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            u.SettingsJson = JsonSerializer.Serialize(req with { Notify = req.Notify ?? [] }, Json);
            await users.UpdateAsync(u);
            return Results.Ok(Read(u));
        });
    }
}
