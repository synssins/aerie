using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.Actions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>
/// HTTP Basic on the Data Center compatible surface only (contracts/behaviour/common.md, Authentication). Each request proves the password again,
/// with Identity's lockout counting the failures. An account that must use one-time codes cannot sign in this way: a password alone would skip
/// the code (common.md, Accounts with sign-in codes). Every other account can, the break-glass account too: each of its requests is a sign-in,
/// audited and announced to the Admins like one through the sign-in page.
/// </summary>
public sealed class BasicAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    UserManager<KbUser> users, SignInManager<KbUser> signIn, KbDbContext db, IAuditLog audit, BasicThrottle throttle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "KbBasic";
    /// <summary>Set when the account is locked: the Retry-After seconds GuardAsync answers 429 with.</summary>
    internal const string LockedItem = "kb.basic.locked";
    private static string? _dummyHash;

    /// <summary>Every refused attempt counts against the caller's address (BasicThrottle).</summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var result = await CheckAsync();
        if (result.Failure is not null) throttle.Fail(BasicThrottle.AddressOf(Context));
        return result;
    }

    private async Task<AuthenticateResult> CheckAsync()
    {
        if (!DcRest.HasBasic(Request)) return AuthenticateResult.NoResult();
        string text;
        try { text = Encoding.UTF8.GetString(Convert.FromBase64String(Request.Headers.Authorization.ToString()[6..].Trim())); }
        catch (FormatException) { return AuthenticateResult.Fail("Not Basic credentials."); }
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return AuthenticateResult.Fail("Not Basic credentials.");
        var (name, password) = (text[..colon], text[(colon + 1)..]);
        var ip = Context.Connection.RemoteIpAddress?.ToString() ?? "";
        // A token in the password field is treated exactly like the Bearer form; sign-in codes do not apply (identity.md, HTTP Basic).
        if (password.StartsWith(OAuthSecrets.AccessPrefix, StringComparison.Ordinal))
            return await ApiTokenAuth.AuthenticateOAuthAsync(Context, db, Context.RequestServices.GetRequiredService<TimeProvider>(), password, SchemeName, ownerName: name);
        if (password.StartsWith(TokenFormat.Prefix, StringComparison.Ordinal))
            return await ApiTokenAuth.AuthenticateAsync(Context, db, Context.RequestServices.GetRequiredService<TimeProvider>(), password, SchemeName, ownerName: name);

        var user = await users.FindByNameAsync(name);
        // Same refusal for every reason, as at sign-in; each is audited so a SIEM sees password guessing. Every refusal before the password
        // check still hashes the password once, so the time taken does not say whether the account exists (identity-plan.md, slice 1).
        if (user is null || user.DeactivatedAt is not null)
            return await RefuseAsync(user, name, user is null ? "unknown user" : "deactivated", ip, password);
        if (await db.GetAsync(RuntimeSettingKeys.CompatBasicPasswordEnabled, Context.RequestAborted) == "false") return await RefuseAsync(user, name, "password Basic turned off", ip, password);
        if (!user.IsBreakGlass && await db.GetAsync(RuntimeSettingKeys.LocalSignInDisabled, Context.RequestAborted) == "true") return await RefuseAsync(user, name, "local sign-in disabled", ip, password);
        // A locked account gets 429 until the lock ends, and its password is not looked at (identity.md, Failed Basic attempts).
        if (await users.IsLockedOutAsync(user) && await users.GetLockoutEndDateAsync(user) is { } end)
        {
            Context.Items[LockedItem] = (int)Math.Max(1, Math.Ceiling((end - Context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()).TotalSeconds));
            return await RefuseAsync(user, name, "locked out", ip);
        }
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded) return await RefuseAsync(user, name, "wrong password", ip);
        // Checked after the password, so the answer tells nothing to someone who does not know it.
        // The same 401 as a wrong password; the audit entry says why.
        if (await Context.RequestServices.GetRequiredService<CodeSignIn>().NeededAsync(user, Context.RequestAborted))
            return await RefuseAsync(user, name, "codes required", ip);
        if (user.IsBreakGlass) await Context.RequestServices.GetRequiredService<BreakGlassService>().OnSignInAsync(user, ip);

        // shortcut: one audit entry per request, as for API tokens; batch them per account if call volume makes the audit lock a bottleneck.
        await audit.AppendAsync(new AuditEvent(AuditActions.BasicUse, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string>
            { ["method"] = Request.Method, ["route"] = (Request.Path.Value ?? "")[..Math.Min(Request.Path.Value?.Length ?? 0, 200)], ["ip"] = ip }), Context.RequestAborted);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <param name="hashLike">When set, the password is hashed against a dummy hash so this refusal takes as long as a password check.</param>
    private async Task<AuthenticateResult> RefuseAsync(KbUser? user, string typed, string reason, string ip, string? hashLike = null)
    {
        if (hashLike is not null)
        {
            var hasher = users.PasswordHasher;
            _dummyHash ??= hasher.HashPassword(new KbUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
            _ = hasher.VerifyHashedPassword(new KbUser(), _dummyHash, hashLike);
        }
        await audit.AppendAsync(new AuditEvent(AuditActions.BasicRefused, user?.Id, "user", user?.Id.ToString(), Data: new Dictionary<string, string>
            { ["userName"] = typed[..Math.Min(typed.Length, 64)], ["reason"] = reason, ["method"] = "basic", ["ip"] = ip }), Context.RequestAborted);
        return AuthenticateResult.Fail("Refused.");
    }

    /// <summary>A 401 carries no Basic challenge, so a browser never shows a password dialog (identity.md, Failed Basic attempts).</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The limit on failed Basic attempts per address (identity.md, Failed Basic attempts): the same number a minute as aerie's sign-in page
/// (Kb:Auth:SignInsPerMinutePerAddress). Past it, <see cref="GateAsync"/> answers 429 before any credential is looked at, so a limited
/// caller cannot go on testing passwords. Only failures count; right credentials and calls without Basic are not held back until then.
/// </summary>
public sealed class BasicThrottle(IOptions<Kb.Core.Config.KbOptions> options, TimeProvider time)
{
    // Each address's window starts at its first failure and lasts a minute, so no clock boundary resets a count midway (a window fixed to the
    // clock's minutes would also let a caller fail twice the limit across one boundary).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Start, int Count)> _failures = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _refusedIn = new(StringComparer.Ordinal);

    private long Now => time.GetUtcNow().ToUnixTimeSeconds();

    private static bool Open(long start, long now) => now - start < 60;

    public static string AddressOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    public void Fail(string address)
    {
        var now = Now;
        _failures.AddOrUpdate(address, (now, 1), (_, e) => Open(e.Start, now) ? (e.Start, e.Count + 1) : (now, 1));
        // shortcut: counted on each server, in memory, like aerie's other limits; ended windows are dropped once many addresses are held.
        if (_failures.Count > 10_000) foreach (var (key, e) in _failures) if (!Open(e.Start, now)) _failures.TryRemove(key, out _);
        if (_refusedIn.Count > 10_000) foreach (var (key, w) in _refusedIn) if (!Open(w, now)) _refusedIn.TryRemove(key, out _);
    }

    /// <summary>True for the first refusal of this address in its current window: what is audited, so a flood writes one entry a window.</summary>
    public bool FirstRefusal(string address)
    {
        var window = _failures.TryGetValue(address, out var e) ? e.Start : Now;
        var first = false;
        _refusedIn.AddOrUpdate(address, _ => { first = true; return window; }, (_, w) => { first = w != window; return window; });
        return first;
    }

    /// <summary>Seconds until the address may try again, or null when it is not limited.</summary>
    public int? RetryAfter(string address)
    {
        var limit = options.Value.Auth.SignInsPerMinutePerAddress;
        var now = Now;
        if (!_failures.TryGetValue(address, out var e) || !Open(e.Start, now) || e.Count < limit) return null;
        return (int)Math.Max(1, e.Start + 60 - now);
    }

    /// <summary>Runs before authentication: a limited address gets 429 with Retry-After and its credentials are not checked.</summary>
    public static async Task GateAsync(HttpContext http, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        if (DcRest.HasBasic(http.Request) && DcRest.IsUnderRoot(http)
            && http.RequestServices.GetRequiredService<BasicThrottle>() is var throttle && throttle.RetryAfter(AddressOf(http)) is { } wait)
        {
            if (throttle.FirstRefusal(AddressOf(http)))
                await http.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.BasicRefused, null, "address", null,
                    Data: new Dictionary<string, string> { ["reason"] = "rate limited", ["method"] = "basic", ["ip"] = AddressOf(http) }), http.RequestAborted);
            http.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await DcRest.Fail(429, "Too many failed sign-ins from this address; try again later.").ExecuteAsync(http);
            return;
        }
        await next(http);
    }
}
