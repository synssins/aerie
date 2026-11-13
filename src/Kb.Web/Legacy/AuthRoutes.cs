using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kb.Web;

public sealed record SignInRequest(string UserName, string Password);
public sealed record MeResponse(Guid Id, string UserName, string DisplayName, bool IsAdmin, bool IsBreakGlass, bool CanAnnounce = false);

public static partial class EndpointMappings
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/local/signin", async (SignInRequest req, SignInManager<KbUser> signIn, UserManager<KbUser> users,
            KbDbContext db, IAuditLog audit, BreakGlassService breakGlass, HttpContext http) =>
        {
            var typed = req.UserName ?? "";
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
            var user = typed.Length == 0 ? null : await users.FindByNameAsync(typed);
            // Same response for unknown user and wrong password: no account enumeration. Both are audited, so a SIEM sees password guessing.
            if (user is null || user.DeactivatedAt is not null)
            {
                await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user?.Id, "user", user?.Id.ToString(), Data: new Dictionary<string, string>
                    { ["userName"] = typed[..Math.Min(typed.Length, 64)], ["reason"] = user is null ? "unknown user" : "deactivated", ["ip"] = ip }));
                return Results.Unauthorized();
            }
            if (!user.IsBreakGlass && await db.GetAsync(RuntimeSettingKeys.LocalSignInDisabled) == "true") return Results.StatusCode(403);

            var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: false, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user.Id, "user", user.Id.ToString(),
                    Data: new Dictionary<string, string> { ["userName"] = user.UserName ?? "", ["reason"] = result.IsLockedOut ? "locked out" : "wrong password", ["lockedOut"] = result.IsLockedOut.ToString(), ["ip"] = ip }));
                return Results.Unauthorized();
            }
            await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "local", ["ip"] = ip }));
            if (user.IsBreakGlass) await breakGlass.OnSignInAsync(user, http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new MeResponse(user.Id, user.UserName!, user.DisplayName, user.IsAdmin, user.IsBreakGlass, user.IsAdmin || user.CanAnnounce));
        }).AllowAnonymous();

        api.MapPost("/auth/signout", async (SignInManager<KbUser> signIn, HttpContext http, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            await signIn.SignOutAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.SignOut, me, "user", me.ToString(), Data: new Dictionary<string, string> { ["ip"] = http.Connection.RemoteIpAddress?.ToString() ?? "" }));
            return Results.NoContent();
        });

        api.MapGet("/me", async (HttpContext http, UserManager<KbUser> users) =>
        {
            var u = await users.FindByIdAsync(http.User.UserId().ToString());
            return u is null ? Results.Unauthorized() : Results.Ok(new MeResponse(u.Id, u.UserName!, u.DisplayName, u.IsAdmin, u.IsBreakGlass, u.IsAdmin || u.CanAnnounce));
        });

    }
}

public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/setup/status", async (SetupService setup, HttpContext http) =>
        {
            var required = await setup.IsRequiredAsync(http.RequestAborted);
            return Results.Ok(new SetupStatus(required, required && !SetupService.IsLocal(http)));
        }).AllowAnonymous();

        api.MapPost("/setup", SetupService.RunAsync).AllowAnonymous();
    }
}

public static partial class UserSettingsEndpoints
{
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
