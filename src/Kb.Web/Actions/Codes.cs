using System.Security.Claims;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web.Actions;

// One-time codes (authenticator apps) at sign-in. Core only: the answers carry the key and the recovery codes, so no plugin may hook them.
// The password step leaves a short-lived "pending" sign-in (Identity's two-factor cookie), never a session; these actions finish it.

/// <summary>The second step of sign-in: a code from the app, or a recovery code (single use). Sent by nobody: the caller is pending.</summary>
[Action("auth.signin.code")]
public sealed record SignInCode : IAction<MeResponse>
{
    public string Code { get; init; } = "";
    public override string ToString() => "SignInCode";
}

/// <summary>Makes a new authenticator key for the caller (pending, or signed in and turning codes on). Refused once codes are set up.</summary>
[Action("auth.codes.key")]
public sealed record NewCodeKey : IAction<CodeKey>;

public sealed record CodeKey(string Key, string Uri)
{
    public override string ToString() => "CodeKey";
}

/// <summary>Confirms the key with one code: codes are on, the recovery codes are shown once, and the caller is signed in.</summary>
[Action("auth.codes.enroll")]
public sealed record EnrollCodes : IAction<CodesEnrolled>
{
    public string Code { get; init; } = "";
    public override string ToString() => "EnrollCodes";
}

public sealed record CodesEnrolled(IReadOnlyList<string> RecoveryCodes, MeResponse Me)
{
    public override string ToString() => $"CodesEnrolled {{ Me = {Me.Id} }}";
}

/// <summary>On: codes are set up. Required: the setting makes this person use them, so they cannot turn them off.
/// Allowed: codes can be set up for this account (not the break-glass account, not one an Admin exempted).</summary>
[Action("me.codes.read", Read = true)]
public sealed record ReadMyCodes : IAction<MyCodes>;

public sealed record MyCodes(bool On, bool Required, bool Allowed = true);

[Action("me.codes.off")]
public sealed record TurnOffMyCodes : IAction<Unit>;

/// <summary>An Admin clears a person's authenticator and recovery codes. Their sessions end; they set up again at the next sign-in.</summary>
[Action("user.codes.reset")]
public sealed record ResetUserCodes : IAction<Unit>
{
    public required Guid UserId { get; init; }
}

[Action("settings.codes.read", Read = true)]
public sealed record ReadCodeSettings : IAction<CodeSettings>;

[Action("settings.codes.set")]
public sealed record SetCodeSettings : IAction<Unit>
{
    public required string RequiredFor { get; init; }
}

/// <summary>RequiredFor: "admins" or "everyone".</summary>
public sealed record CodeSettings(string RequiredFor);

/// <summary>Who must use codes, the pending sign-in, and finishing it.</summary>
internal sealed class CodeSignIn(KbDbContext db, UserManager<KbUser> users, SignInManager<KbUser> signIn, IHttpContextAccessor http,
    IUserStore<KbUser> store, IOptions<KbOptions> options, Kb.Core.Plugins.PluginRegistry plugins)
{
    public const string Admins = "admins", Everyone = "everyone";
    // Identity's own names for where the key and the recovery codes are kept.
    private const string StoreName = "[AspNetUserStore]";

    public async Task<string> RequiredForAsync(CancellationToken ct) => await db.GetAsync(RuntimeSettingKeys.CodesRequiredFor, ct) == Everyone ? Everyone : Admins;

    /// <summary>The setting makes this person use codes. The break-glass account never does: its sign-in is audited and alerts the Admins.</summary>
    /// An account an Admin exempted never does, unless it is an Admin's.
    public async Task<bool> RequiredByPolicyAsync(KbUser u, CancellationToken ct) =>
        !u.IsBreakGlass && (u.IsAdmin || !u.CodesExempt && await RequiredForAsync(ct) == Everyone);

    /// <summary>This sign-in needs a code: the setting says so, the person chose codes, or codes are set up.</summary>
    public async Task<bool> NeededAsync(KbUser u, CancellationToken ct) =>
        !u.IsBreakGlass && (u.IsAdmin || !u.CodesExempt && (u.CodesRequired || u.TwoFactorEnabled || await RequiredByPolicyAsync(u, ct)));

    /// <summary>Codes cannot be set up for this account: an Admin exempted it (a shared demo account could otherwise lock its other users out).</summary>
    public static bool Barred(KbUser u) => u.CodesExempt && !u.IsAdmin;

    /// <summary>The password was right: leave a pending sign-in and say which step comes next (409 with { step }).</summary>
    public async Task<Problem> PendAsync(KbUser u)
    {
        var id = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        id.AddClaim(new Claim(ClaimTypes.Name, u.Id.ToString()));
        await http.HttpContext!.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(id));
        return Problem.Conflict(body: new { step = u.TwoFactorEnabled ? "code" : "enroll" });
    }

    /// <summary>The person whose password was right a moment ago, or null.</summary>
    public async Task<KbUser?> PendingAsync() => await signIn.GetTwoFactorAuthenticationUserAsync() is { DeactivatedAt: null, IsBreakGlass: false } u ? u : null;

    /// <summary>Ends the pending sign-in and opens a session.</summary>
    public async Task FinishAsync(KbUser u)
    {
        await http.HttpContext!.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await signIn.SignInWithClaimsAsync(u, isPersistent: false, [new Claim("amr", "mfa")]);
    }

    public async Task<string> NewKeyAsync(KbUser u)
    {
        var key = users.GenerateNewAuthenticatorKey();
        // Through the store, not ResetAuthenticatorKeyAsync: that changes the security stamp, which would end the session that turns codes on.
        await ((IUserAuthenticatorKeyStore<KbUser>)store).SetAuthenticatorKeyAsync(u, key, CancellationToken.None);
        await users.UpdateAsync(u);
        return key;
    }

    /// <summary>Codes off and the key and recovery codes gone. Changes the security stamp, so every session of the person ends.</summary>
    public async Task ClearAsync(KbUser u)
    {
        await users.RemoveAuthenticationTokenAsync(u, StoreName, "AuthenticatorKey");
        await users.RemoveAuthenticationTokenAsync(u, StoreName, "RecoveryCodes");
        await users.SetTwoFactorEnabledAsync(u, false);
    }

    public static string Uri(KbUser u, string key, string issuer) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(u.UserName ?? "")}?secret={key}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6";

    public async Task<string> IssuerAsync(CancellationToken ct) => await db.GetAsync(BrandingEndpoints.ProductNameKey, ct) ?? options.Value.Brand.ProductName;

    public async Task<MeResponse> MeAsync(KbUser u, CancellationToken ct) =>
        new(u.Id, u.UserName!, u.DisplayName, u.IsAdmin, u.IsBreakGlass, u.IsAdmin || u.CanAnnounce, await PluginPermissions.HeldAsync(db, plugins, u.Id, u.IsAdmin, ct));

    /// <summary>An app code is six digits; anything else is taken as a recovery code.</summary>
    public static string Clean(string? code) => new([.. (code ?? "").Where(c => !char.IsWhiteSpace(c))]);
    public static bool IsAppCode(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);
}

internal sealed class SignInCodeHandler(CodeSignIn codes, UserManager<KbUser> users, IAuditLog audit, TimeProvider time) : IActionHandler<SignInCode, MeResponse>
{
    // Nobody is signed in yet; the pending sign-in and the code are checked as the work.
    public Task<Problem?> AuthorizeAsync(ActionContext<SignInCode> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<MeResponse>> HandleAsync(ActionContext<SignInCode> ctx)
    {
        var ip = ctx.Actor.RemoteIp ?? "";
        if (await codes.PendingAsync() is not { TwoFactorEnabled: true } user) return Problem.Unauthorized;
        var code = CodeSignIn.Clean(ctx.Action.Code);
        var app = CodeSignIn.IsAppCode(code);
        // A code from the app is good once: its time step, and every earlier one, is not taken again after it was accepted.
        var step = app ? CodeSteps.StepOf(await users.GetAuthenticatorKeyAsync(user), code, time.GetUtcNow()) : null;
        var used = step is { } at && long.TryParse(await users.GetAuthenticationTokenAsync(user, CodeSteps.Provider, CodeSteps.Name), System.Globalization.CultureInfo.InvariantCulture, out var last) && at <= last;
        var ok = !await users.IsLockedOutAsync(user) && !used && (app
            ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code)
            : code.Length > 0 && (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.ToUpperInvariant())).Succeeded);
        if (!ok)
        {
            // A wrong code counts toward the lockout like a wrong password.
            if (!await users.IsLockedOutAsync(user)) await users.AccessFailedAsync(user);
            var locked = await users.IsLockedOutAsync(user);
            await audit.AppendAsync(new AuditEvent(AuditActions.CodeFailed, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string>
                { ["kind"] = app ? "app" : "recovery", ["lockedOut"] = locked.ToString(), ["ip"] = ip }));
            return Problem.Unauthorized;
        }
        await users.ResetAccessFailedCountAsync(user);
        if (step is { } accepted) await users.SetAuthenticationTokenAsync(user, CodeSteps.Provider, CodeSteps.Name, accepted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await codes.FinishAsync(user);
        await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string>
            { ["method"] = app ? "local+code" : "local+recovery", ["ip"] = ip }));
        return await codes.MeAsync(user, ctx.Cancel);
    }
}

/// <summary>Which 30-second step a code from an authenticator app belongs to (RFC 6238, as the apps compute it).</summary>
public static class CodeSteps
{
    public const string Provider = "kb", Name = "code.step";

    /// <summary>The step, of the five around now, whose code this is; null when it is none of them or there is no key.</summary>
    public static long? StepOf(string? base32Key, string code, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(base32Key) || code is not { Length: 6 }) return null;
        var key = Base32(base32Key);
        var current = now.ToUnixTimeSeconds() / 30;
        foreach (var step in new[] { current, current - 1, current + 1, current - 2, current + 2 })
        {
            var counter = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian) Array.Reverse(counter);
#pragma warning disable CA5350 // RFC 6238 as every authenticator app computes it; the key is the secret, not the hash's strength
            var hash = System.Security.Cryptography.HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
            var o = hash[^1] & 0xf;
            var n = ((hash[o] & 0x7f) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes((n % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture)), System.Text.Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var (bits, value, bytes) = (0, 0, new List<byte>());
        foreach (var ch in text.ToUpperInvariant())
        {
            var index = alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (index < 0) continue; // padding and spaces
            (value, bits) = ((value << 5) | index, bits + 5);
            if (bits >= 8) { bytes.Add((byte)(value >> (bits - 8))); bits -= 8; }
        }
        return [.. bytes];
    }
}

/// <summary>The person setting up codes: the one signed in with a session (not a token), or else the pending one.</summary>
internal static class Enrolling
{
    public static async Task<(KbUser? User, bool Pending)> WhoAsync(Actor actor, UserManager<KbUser> users, CodeSignIn codes)
    {
        if (actor.TokenId is not null) return (null, false);
        if (actor.UserId is { } id) return (await users.FindByIdAsync(id.ToString()) is { IsBreakGlass: false } u ? u : null, false);
        return (await codes.PendingAsync(), true);
    }
}

internal sealed class NewCodeKeyHandler(CodeSignIn codes, UserManager<KbUser> users) : IActionHandler<NewCodeKey, CodeKey>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<NewCodeKey> ctx)
    {
        var (u, pending) = await Enrolling.WhoAsync(ctx.Actor, users, codes);
        if (u is null) return ctx.Actor.TokenId is null ? Problem.Unauthorized : Problem.Forbidden;
        if (pending && await users.IsLockedOutAsync(u)) return Problem.Unauthorized;
        _user = u;
        return null;
    }

    public async Task<Outcome<CodeKey>> HandleAsync(ActionContext<NewCodeKey> ctx)
    {
        // Set up already: a new key would replace the one in the person's app.
        if (_user!.TwoFactorEnabled) return Problem.Conflict("Codes are already set up.");
        if (CodeSignIn.Barred(_user)) return Problem.Conflict("Codes are off for this account.");
        var key = await codes.NewKeyAsync(_user);
        return new CodeKey(key, CodeSignIn.Uri(_user, key, await codes.IssuerAsync(ctx.Cancel)));
    }
}

internal sealed class EnrollCodesHandler(CodeSignIn codes, UserManager<KbUser> users, SignInManager<KbUser> signIn, IAuditLog audit) : IActionHandler<EnrollCodes, CodesEnrolled>
{
    private KbUser? _user;
    private bool _pending;

    public async Task<Problem?> AuthorizeAsync(ActionContext<EnrollCodes> ctx)
    {
        (_user, _pending) = await Enrolling.WhoAsync(ctx.Actor, users, codes);
        if (_user is null) return ctx.Actor.TokenId is null ? Problem.Unauthorized : Problem.Forbidden;
        return _pending && await users.IsLockedOutAsync(_user) ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<CodesEnrolled>> HandleAsync(ActionContext<EnrollCodes> ctx)
    {
        var u = _user!;
        var ip = ctx.Actor.RemoteIp ?? "";
        if (u.TwoFactorEnabled) return Problem.Conflict("Codes are already set up.");
        if (CodeSignIn.Barred(u)) return Problem.Conflict("Codes are off for this account.");
        var code = CodeSignIn.Clean(ctx.Action.Code);
        if (await users.GetAuthenticatorKeyAsync(u) is null || !CodeSignIn.IsAppCode(code)
            || !await users.VerifyTwoFactorTokenAsync(u, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            // Only a pending sign-in counts toward the lockout: someone with a session already proved who they are.
            if (_pending) await users.AccessFailedAsync(u);
            await audit.AppendAsync(new AuditEvent(AuditActions.CodeFailed, u.Id, "user", u.Id.ToString(), Data: new Dictionary<string, string>
                { ["kind"] = "setup", ["ip"] = ip }));
            return Problem.Invalid("code", "Wrong code.");
        }
        if (!_pending) { u.CodesRequired = true; await users.UpdateAsync(u); }
        await users.SetTwoFactorEnabledAsync(u, true);
        var recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(u, 10))!.ToList();
        await users.ResetAccessFailedCountAsync(u);
        await audit.AppendAsync(new AuditEvent(AuditActions.CodesEnroll, u.Id, "user", u.Id.ToString(), Data: new Dictionary<string, string> { ["ip"] = ip }));
        // Turning codes on changed the security stamp, which ends every session; this one is opened again.
        if (_pending)
        {
            await codes.FinishAsync(u);
            await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, u.Id, "user", u.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "local+code", ["ip"] = ip }));
        }
        else await signIn.SignInAsync(u, isPersistent: false);
        return new CodesEnrolled(recovery, await codes.MeAsync(u, ctx.Cancel));
    }
}

internal sealed class ReadMyCodesHandler(CodeSignIn codes, UserManager<KbUser> users) : IActionHandler<ReadMyCodes, MyCodes>
{
    private KbUser? _me;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadMyCodes> ctx) =>
        ctx.Actor.UserId is not { } id || (_me = await users.FindByIdAsync(id.ToString())) is null ? Problem.Unauthorized : null;

    public async Task<Outcome<MyCodes>> HandleAsync(ActionContext<ReadMyCodes> ctx) =>
        new MyCodes(_me!.TwoFactorEnabled, await codes.RequiredByPolicyAsync(_me, ctx.Cancel), !_me.IsBreakGlass && !CodeSignIn.Barred(_me));
}

internal sealed class TurnOffMyCodesHandler(CodeSignIn codes, UserManager<KbUser> users, SignInManager<KbUser> signIn, IHttpContextAccessor http, TimeProvider time) : IActionHandler<TurnOffMyCodes, Unit>
{
    private static readonly TimeSpan Recent = TimeSpan.FromMinutes(10);
    private KbUser? _me;

    public async Task<Problem?> AuthorizeAsync(ActionContext<TurnOffMyCodes> ctx)
    {
        if (ctx.Actor.UserId is not { } id) return Problem.Unauthorized;
        // A token cannot weaken how its owner signs in.
        if (ctx.Actor.TokenId is not null) return Problem.Forbidden;
        return (_me = await users.FindByIdAsync(id.ToString())) is null ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<TurnOffMyCodes> ctx)
    {
        var u = _me!;
        if (await codes.RequiredByPolicyAsync(u, ctx.Cancel)) return Problem.Conflict("Codes are required for this account.");
        // Whoever sits down at an open session must not be able to take the second step away: turning codes off needs a sign-in
        // (password and code) made just now.
        if (u.TwoFactorEnabled && !SessionAge.OpenedWithin(http.HttpContext, Recent, time.GetUtcNow()))
            return new Problem(403, "Sign out and sign in again, then turn codes off within 10 minutes.");
        u.CodesRequired = false;
        await users.UpdateAsync(u);
        await codes.ClearAsync(u);
        ctx.Audit(AuditActions.CodesOff, "user", u.Id.ToString());
        // Clearing changed the stamp, which ends every session; this one stays open.
        await signIn.SignInAsync(u, isPersistent: false);
        return new Unit();
    }
}

internal sealed class ResetUserCodesHandler(KbDbContext db, CodeSignIn codes, UserManager<KbUser> users, IAccess access) : IActionHandler<ResetUserCodes, Unit>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ResetUserCodes> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.UserId;
        return await db.Users.AnyAsync(u => u.Id == id && u.DeletedAt == null, ctx.Cancel)
            && (_user = await users.FindByIdAsync(id.ToString())) is not null ? null : Problem.NotFound;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<ResetUserCodes> ctx)
    {
        await codes.ClearAsync(_user!);
        ctx.Audit(AuditActions.UserCodesReset, "user", _user!.Id.ToString());
        return new Unit();
    }
}

/// <summary>An Admin turns codes off (or on again) for one account that is not an Admin's, such as a shared read-only demo account.</summary>
[Action("user.codes.exempt")]
public sealed record SetUserCodesExempt : IAction<Unit>
{
    public required Guid UserId { get; init; }
    public required bool Exempt { get; init; }
}

public sealed record CodesExemptRequest(bool Exempt);

internal sealed class SetUserCodesExemptHandler(KbDbContext db, CodeSignIn codes, UserManager<KbUser> users, IAccess access) : IActionHandler<SetUserCodesExempt, Unit>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SetUserCodesExempt> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.UserId;
        return await db.Users.AnyAsync(u => u.Id == id && u.DeletedAt == null, ctx.Cancel)
            && (_user = await users.FindByIdAsync(id.ToString())) is not null ? null : Problem.NotFound;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetUserCodesExempt> ctx)
    {
        var u = _user!;
        if (u.IsAdmin || u.IsBreakGlass) return Problem.Conflict("An Admin always uses codes.");
        u.CodesExempt = ctx.Action.Exempt;
        if (u.CodesExempt) u.CodesRequired = false;
        await users.UpdateAsync(u);
        // Codes the person had set up go too, so nobody is asked for them; this ends their sessions.
        if (u.CodesExempt && (u.TwoFactorEnabled || await users.GetAuthenticatorKeyAsync(u) is not null)) await codes.ClearAsync(u);
        ctx.Audit(AuditActions.UserCodesExempt, "user", u.Id.ToString(), data: new Dictionary<string, string> { ["exempt"] = u.CodesExempt.ToString() });
        return new Unit();
    }
}

internal sealed class ReadCodeSettingsHandler(CodeSignIn codes, IAccess access) : IActionHandler<ReadCodeSettings, CodeSettings>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadCodeSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<CodeSettings>> HandleAsync(ActionContext<ReadCodeSettings> ctx) => new CodeSettings(await codes.RequiredForAsync(ctx.Cancel));
}

internal sealed class SetCodeSettingsHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SetCodeSettings, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SetCodeSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetCodeSettings> ctx)
    {
        var value = ctx.Action.RequiredFor;
        if (value is not (CodeSignIn.Admins or CodeSignIn.Everyone)) return Problem.Invalid("requiredFor", "admins or everyone.");
        await db.SetAsync(RuntimeSettingKeys.CodesRequiredFor, value, ctx.Actor.UserId, time.GetUtcNow(), ctx.Cancel);
        ctx.Audit(AuditActions.SettingsChange, "setting", RuntimeSettingKeys.CodesRequiredFor, data: new Dictionary<string, string> { ["value"] = value });
        return new Unit();
    }
}
