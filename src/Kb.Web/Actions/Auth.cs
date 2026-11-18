using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;

namespace Kb.Web.Actions;

/// <summary>
/// Sets a local account's password with the link an Admin handed over. Core only: it carries the token and the new password.
/// Nobody is signed in when it is sent.
/// </summary>
[Action("auth.password.set")]
public sealed record SetPassword : IAction<Unit>
{
    public required Guid UserId { get; init; }
    public required string Token { get; init; }
    public required string Password { get; init; }

    public override string ToString() => $"SetPassword {{ UserId = {UserId} }}";
}

internal sealed class SetPasswordHandler(UserManager<KbUser> users, IAuditLog audit) : IActionHandler<SetPassword, Unit>
{
    // The link is the right to do this; it is checked as the work, as a password is at sign-in.
    public Task<Problem?> AuthorizeAsync(ActionContext<SetPassword> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetPassword> ctx)
    {
        var (req, ip) = (ctx.Action, ctx.Actor.RemoteIp ?? "");
        var user = await users.FindByIdAsync(req.UserId.ToString());
        // One answer for every reason, so it does not tell which accounts exist. The break-glass password is the server's secret.
        async Task<Outcome<Unit>> RefusedAsync(string reason)
        {
            await audit.AppendAsync(new AuditEvent(AuditActions.PasswordSetFailed, user?.Id, "user", user?.Id.ToString(), Data: new Dictionary<string, string> { ["reason"] = reason, ["ip"] = ip }));
            return new Problem(400, "The link is not valid.");
        }
        if (user is null || user.DeactivatedAt is not null || user.IsBreakGlass || user.Source != UserSource.Local) return await RefusedAsync("no such account");
        // An account linked to a sign-in method is entered through that method only.
        if (user.ExternalIssuer is not null) return await RefusedAsync("linked to a sign-in method");
        if (string.IsNullOrEmpty(req.Token) || !await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider, UserManager<KbUser>.ResetPasswordTokenPurpose, req.Token))
            return await RefusedAsync("wrong or used link");

        var result = await users.ResetPasswordAsync(user, req.Token, req.Password ?? "");
        if (!result.Succeeded) return new Problem(400, Fields: new Dictionary<string, string[]> { ["password"] = [.. result.Errors.Select(e => e.Description)] });
        // A fresh start: earlier failed sign-ins do not keep the account locked. The new stamp ends every open session.
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await audit.AppendAsync(new AuditEvent(AuditActions.PasswordSet, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "link" }));
        return new Unit();
    }
}

internal sealed class SignInHandler(SignInManager<KbUser> signIn, UserManager<KbUser> users, KbDbContext db, IAuditLog audit, BreakGlassService breakGlass, CodeSignIn codes)
    : IActionHandler<SignIn, MeResponse>
{
    private static string? _dummyHash;

    // Nobody is signed in yet, so there is nothing to permit: the name and the password are checked as the work.
    // A plugin's refusal therefore comes before the account is looked up, and is the same for a real name and a made-up one.
    public Task<Problem?> AuthorizeAsync(ActionContext<SignIn> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<MeResponse>> HandleAsync(ActionContext<SignIn> ctx)
    {
        var req = ctx.Action;
        var typed = req.UserName ?? "";
        var ip = ctx.Actor.RemoteIp ?? "";
        var user = typed.Length == 0 ? null : await users.FindByNameAsync(typed);
        // The audit entries name the account, not the caller (who is nobody yet), so they are written here and not through ctx.Audit.
        // With local sign-in turned off the answer is the same for every name but the break-glass account's, a made-up name included.
        if (user?.IsBreakGlass != true && await db.GetAsync(RuntimeSettingKeys.LocalSignInDisabled, ctx.Cancel) == "true") return Problem.Forbidden;
        // Same response for unknown user and wrong password: no account enumeration. Both are audited, so a SIEM sees password guessing.
        if (user is null || user.DeactivatedAt is not null)
        {
            // The password is hashed once here too, so the time taken does not say whether the account exists.
            _dummyHash ??= users.PasswordHasher.HashPassword(new KbUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
            _ = users.PasswordHasher.VerifyHashedPassword(new KbUser(), _dummyHash, req.Password ?? "");
            await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user?.Id, "user", user?.Id.ToString(), Data: new Dictionary<string, string>
                { ["userName"] = typed[..Math.Min(typed.Length, 64)], ["reason"] = user is null ? "unknown user" : "deactivated", ["ip"] = ip }));
            return Problem.Unauthorized;
        }

        // The password is checked without opening a session: someone who must use codes gets a pending sign-in instead.
        var result = await signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user.Id, "user", user.Id.ToString(),
                Data: new Dictionary<string, string> { ["userName"] = user.UserName ?? "", ["reason"] = result.IsLockedOut ? "locked out" : "wrong password", ["lockedOut"] = result.IsLockedOut.ToString(), ["ip"] = ip }));
            return Problem.Unauthorized;
        }
        // An account linked to a sign-in method is entered through that method only. Checked after the password, with the same answer
        // as a wrong one, so it tells nobody that the account is linked.
        if (user.ExternalIssuer is not null)
        {
            await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user.Id, "user", user.Id.ToString(),
                Data: new Dictionary<string, string> { ["userName"] = user.UserName ?? "", ["reason"] = "linked to a sign-in method", ["ip"] = ip }));
            return Problem.Unauthorized;
        }
        if (await codes.NeededAsync(user, ctx.Cancel)) return await codes.PendAsync(user);
        await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: "pwd");
        await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "local", ["ip"] = ip }));
        if (user.IsBreakGlass) await breakGlass.OnSignInAsync(user, ctx.Actor.RemoteIp);
        // The same answer as me.read: a plugin's script is handed this person without another request in between.
        return await codes.MeAsync(user, ctx.Cancel);
    }
}

internal sealed class SignOutHandler(SignInManager<KbUser> signIn, UserManager<KbUser> users, IAuditLog audit) : IActionHandler<SignOut, Unit>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<SignOut> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SignOut> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        // A new security stamp fails every cookie issued before it (checked on each request), so a copied cookie dies too.
        // Owner's choice: signing out anywhere ends all of this user's sessions.
        if (await users.FindByIdAsync(me.ToString()) is { } user) await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        // Written here, as sign-in's entries are: the action changes who is signed in.
        await audit.AppendAsync(new AuditEvent(AuditActions.SignOut, me, "user", me.ToString(), Data: new Dictionary<string, string> { ["ip"] = ctx.Actor.RemoteIp ?? "" }));
        return new Unit();
    }
}

internal sealed class ReadMeHandler(UserManager<KbUser> users, KbDbContext db, Kb.Core.Plugins.PluginRegistry plugins, AiService ai) : IActionHandler<ReadMe, MeResponse>
{
    private KbUser? _me;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadMe> ctx) =>
        ctx.Actor.UserId is not { } id || (_me = await users.FindByIdAsync(id.ToString())) is null ? Problem.Unauthorized : null;

    public async Task<Outcome<MeResponse>> HandleAsync(ActionContext<ReadMe> ctx)
    {
        var u = _me!;
        // A request made with an API token holds no global permission, as it is never an Admin's.
        var held = ctx.Actor.TokenId is null ? await PluginPermissions.HeldAsync(db, plugins, u.Id, u.IsAdmin, ctx.Cancel) : [];
        return new MeResponse(u.Id, u.UserName!, u.DisplayName, u.IsAdmin, u.IsBreakGlass, u.IsAdmin || u.CanAnnounce, held, await AiSearch.AvailableAsync(ai, ctx.Actor, ctx.Cancel));
    }
}

internal sealed class ReadMySettingsHandler(UserManager<KbUser> users) : IActionHandler<ReadMySettings, UserSettings>
{
    private KbUser? _me;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadMySettings> ctx) =>
        ctx.Actor.UserId is not { } id || (_me = await users.FindByIdAsync(id.ToString())) is null ? Problem.Unauthorized : null;

    public Task<Outcome<UserSettings>> HandleAsync(ActionContext<ReadMySettings> ctx)
    {
        var s = UserSettingsModel.Read(_me!);
        // A missing or empty document means defaults.
        return Task.FromResult<Outcome<UserSettings>>(string.IsNullOrEmpty(s.Theme) ? UserSettings.Default : s);
    }
}

internal sealed class SaveMySettingsHandler(UserManager<KbUser> users) : IActionHandler<SaveMySettings, UserSettings>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<SaveMySettings> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<UserSettings>> HandleAsync(ActionContext<SaveMySettings> ctx)
    {
        var req = ctx.Action;
        var errors = new Dictionary<string, string[]>();
        if (!UserSettingsModel.Themes.Contains(req.Theme)) errors["theme"] = ["light, dark or system."];
        if (!UserSettingsModel.Widths.Contains(req.Width)) errors["width"] = ["boxed or full."];
        if (req.StartPage != "last" && !Guid.TryParse(req.StartPage, out _)) errors["startPage"] = ["last or a space id."];
        if (string.IsNullOrWhiteSpace(req.CodeLanguage) || req.CodeLanguage.Length > 32) errors["codeLanguage"] = ["1-32 characters."];
        if ((req.Notify?.Count ?? 0) > 50) errors["notify"] = ["Too many entries."];
        if (errors.Count > 0) return new Problem(400, Fields: errors);

        // As before, the settings are checked first: a caller whose account is gone hears 401 only for settings that are valid.
        var u = await users.FindByIdAsync(ctx.Actor.UserId!.Value.ToString());
        if (u is null) return Problem.Unauthorized;
        u.SettingsJson = JsonSerializer.Serialize(new UserSettings(req.Theme, req.Width, req.StartPage, req.CodeLanguage, req.Notify ?? []), UserSettingsModel.Json);
        await users.UpdateAsync(u);
        return UserSettingsModel.Read(u);
    }
}

internal sealed class ReadSetupStatusHandler(SetupService setup, IHttpContextAccessor http) : IActionHandler<ReadSetupStatus, SetupStatus>
{
    // Asked before anyone can sign in.
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadSetupStatus> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<SetupStatus>> HandleAsync(ActionContext<ReadSetupStatus> ctx)
    {
        var required = await setup.IsRequiredAsync(ctx.Cancel);
        return new SetupStatus(required, required && !SetupService.IsLocal(http.HttpContext));
    }
}

internal sealed class RunSetupHandler(SetupService setup, CodeSignIn codes, IHttpContextAccessor http) : IActionHandler<RunSetup, MeResponse>
{
    // Nobody is signed in yet. Whether setup is still open and whether the code fits are checked with the work, inside the service's lock.
    public Task<Problem?> AuthorizeAsync(ActionContext<RunSetup> ctx) => Task.FromResult<Problem?>(null);

    public Task<Outcome<MeResponse>> HandleAsync(ActionContext<RunSetup> ctx) =>
        setup.RunAsync(ctx.Action, SetupService.IsLocal(http.HttpContext), codes, ctx.Cancel);
}
