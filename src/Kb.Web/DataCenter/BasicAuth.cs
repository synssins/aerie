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
        if (password.StartsWith(TokenFormat.Prefix, StringComparison.Ordinal))
            return await ApiTokenAuth.AuthenticateAsync(Context, db, Context.RequestServices.GetRequiredService<TimeProvider>(), password, SchemeName, ownerName: name);

        var user = await users.FindByNameAsync(name);
        // Same refusal for every reason, as at sign-in; each is audited so a SIEM sees password guessing.
        if (user is null || user.DeactivatedAt is not null)
            return await RefuseAsync(user, name, user is null ? "unknown user" : "deactivated", ip);
        if (!user.IsBreakGlass && await db.GetAsync(RuntimeSettingKeys.LocalSignInDisabled, Context.RequestAborted) == "true") return await RefuseAsync(user, name, "local sign-in disabled", ip);
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded) return await RefuseAsync(user, name, result.IsLockedOut ? "locked out" : "wrong password", ip);
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

    private async Task<AuthenticateResult> RefuseAsync(KbUser? user, string typed, string reason, string ip)
    {
        await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user?.Id, "user", user?.Id.ToString(), Data: new Dictionary<string, string>
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> _failures = new(StringComparer.Ordinal);

    private long Window => time.GetUtcNow().ToUnixTimeSeconds() / 60;

    public static string AddressOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    public void Fail(string address)
    {
        var window = Window;
        _failures.AddOrUpdate(address, (window, 1), (_, e) => e.Window == window ? (window, e.Count + 1) : (window, 1));
        // shortcut: counted on each server, in memory, like aerie's other limits; old windows are dropped once many addresses are held.
        if (_failures.Count > 10_000) foreach (var (key, e) in _failures) if (e.Window != window) _failures.TryRemove(key, out _);
    }

    /// <summary>Seconds until the address may try again, or null when it is not limited.</summary>
    public int? RetryAfter(string address)
    {
        var limit = options.Value.Auth.SignInsPerMinutePerAddress;
        if (!_failures.TryGetValue(address, out var e) || e.Window != Window || e.Count < limit) return null;
        return (int)Math.Max(1, (Window + 1) * 60 - time.GetUtcNow().ToUnixTimeSeconds());
    }

    /// <summary>Runs before authentication: a limited address gets 429 with Retry-After and its credentials are not checked.</summary>
    public static async Task GateAsync(HttpContext http, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        if (DcRest.HasBasic(http.Request) && DcRest.IsUnderRoot(http)
            && http.RequestServices.GetRequiredService<BasicThrottle>().RetryAfter(AddressOf(http)) is { } wait)
        {
            http.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await DcRest.Fail(429, "Too many failed sign-ins from this address; try again later.").ExecuteAsync(http);
            return;
        }
        await next(http);
    }
}
