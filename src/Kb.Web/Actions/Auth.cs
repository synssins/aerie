using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;

namespace Kb.Web.Actions;

internal sealed class SignInHandler(SignInManager<KbUser> signIn, UserManager<KbUser> users, KbDbContext db, IAuditLog audit, BreakGlassService breakGlass)
    : IActionHandler<SignIn, MeResponse>
{
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
        // Same response for unknown user and wrong password: no account enumeration. Both are audited, so a SIEM sees password guessing.
        if (user is null || user.DeactivatedAt is not null)
        {
            await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user?.Id, "user", user?.Id.ToString(), Data: new Dictionary<string, string>
                { ["userName"] = typed[..Math.Min(typed.Length, 64)], ["reason"] = user is null ? "unknown user" : "deactivated", ["ip"] = ip }));
            return Problem.Unauthorized;
        }
        if (!user.IsBreakGlass && await db.GetAsync(RuntimeSettingKeys.LocalSignInDisabled, ctx.Cancel) == "true") return Problem.Forbidden;

        var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user.Id, "user", user.Id.ToString(),
                Data: new Dictionary<string, string> { ["userName"] = user.UserName ?? "", ["reason"] = result.IsLockedOut ? "locked out" : "wrong password", ["lockedOut"] = result.IsLockedOut.ToString(), ["ip"] = ip }));
            return Problem.Unauthorized;
        }
        await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "local", ["ip"] = ip }));
        if (user.IsBreakGlass) await breakGlass.OnSignInAsync(user, ctx.Actor.RemoteIp);
        return new MeResponse(user.Id, user.UserName!, user.DisplayName, user.IsAdmin, user.IsBreakGlass, user.IsAdmin || user.CanAnnounce);
    }
}

internal sealed class SignOutHandler(SignInManager<KbUser> signIn, IAuditLog audit) : IActionHandler<SignOut, Unit>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<SignOut> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SignOut> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        await signIn.SignOutAsync();
        // Written here, as sign-in's entries are: the action changes who is signed in.
        await audit.AppendAsync(new AuditEvent(AuditActions.SignOut, me, "user", me.ToString(), Data: new Dictionary<string, string> { ["ip"] = ctx.Actor.RemoteIp ?? "" }));
        return new Unit();
    }
}

internal sealed class ReadMeHandler(UserManager<KbUser> users, KbDbContext db, Kb.Core.Plugins.PluginRegistry plugins) : IActionHandler<ReadMe, MeResponse>
{
    private KbUser? _me;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadMe> ctx) =>
        ctx.Actor.UserId is not { } id || (_me = await users.FindByIdAsync(id.ToString())) is null ? Problem.Unauthorized : null;

    public async Task<Outcome<MeResponse>> HandleAsync(ActionContext<ReadMe> ctx)
    {
        var u = _me!;
        // A request made with an API token holds no global permission, as it is never an Admin's.
        var held = ctx.Actor.TokenId is null ? await PluginPermissions.HeldAsync(db, plugins, u.Id, u.IsAdmin, ctx.Cancel) : [];
        return new MeResponse(u.Id, u.UserName!, u.DisplayName, u.IsAdmin, u.IsBreakGlass, u.IsAdmin || u.CanAnnounce, held);
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

internal sealed class RunSetupHandler(SetupService setup, SignInManager<KbUser> signIn, IHttpContextAccessor http) : IActionHandler<RunSetup, MeResponse>
{
    // Nobody is signed in yet. Whether setup is still open and whether the code fits are checked with the work, inside the service's lock.
    public Task<Problem?> AuthorizeAsync(ActionContext<RunSetup> ctx) => Task.FromResult<Problem?>(null);

    public Task<Outcome<MeResponse>> HandleAsync(ActionContext<RunSetup> ctx) =>
        setup.RunAsync(ctx.Action, SetupService.IsLocal(http.HttpContext), signIn, ctx.Cancel);
}
