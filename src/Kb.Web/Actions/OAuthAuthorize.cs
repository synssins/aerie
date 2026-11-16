using System.Text;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>Where the browser goes next, or the error page shown instead when client or redirect URI is not acceptable (never redirected).</summary>
public sealed record OAuthAuthorizeResult(string? Location, string? ErrorPage);

/// <summary>
/// <c>GET /rest/oauth2/latest/authorize</c> (identity.md, The authorization request): checks the request, keeps it pending for ten minutes,
/// and sends the browser to aerie's consent page, or straight back with a code when the user's grant already covers the scopes.
/// </summary>
[Action("oauth.authorize")]
public sealed record DcOAuthAuthorize : IAction<OAuthAuthorizeResult>
{
    /// <summary>The query string, each name with every value it was sent with.</summary>
    public required IReadOnlyDictionary<string, string[]> Query { get; init; }
}

public sealed record OAuthAuthorizeRequestDto(Guid Id, string ClientName, IReadOnlyList<string> Scopes, IReadOnlyList<string> AlreadyGranted, DateTimeOffset ExpiresAt);

public sealed record OAuthDecisionRequest(bool Allow);

public sealed record OAuthDecisionDto(string RedirectTo);

/// <summary>Aerie's consent page reads a pending request of the signed-in user. 404 when it is someone else's, expired or decided.</summary>
[Action("oauth.request.read", Read = true)]
public sealed record ReadOAuthAuthorizeRequest : IAction<OAuthAuthorizeRequestDto>
{
    public required Guid Id { get; init; }
}

/// <summary>The user allows or denies a pending request; the answer says where to send the browser. Needs the session cookie.</summary>
[Action("oauth.request.decide")]
public sealed record DecideOAuthAuthorizeRequest : IAction<OAuthDecisionDto>
{
    public required Guid Id { get; init; }
    public required bool Allow { get; init; }
}

/// <summary>What the authorize endpoint and the consent decision share.</summary>
internal static partial class OAuthFlow
{
    public static readonly TimeSpan RequestLife = TimeSpan.FromMinutes(10);

    [GeneratedRegex("^[A-Za-z0-9._~-]{43,128}$")]
    public static partial Regex PkceValue();

    /// <summary>The redirect URI with these parameters added to its query (RFC 6749 section 4.1.2): nothing ever goes in a fragment.</summary>
    public static string Append(string uri, params (string Name, string? Value)[] pairs)
    {
        var b = new StringBuilder(uri);
        var sep = uri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        foreach (var (name, value) in pairs)
        {
            if (value is null) continue;
            b.Append(sep).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
        return b.ToString();
    }

    /// <summary>
    /// Records the consent (the grant's scopes only grow) and makes a single-use code bound to client, user, redirect URI, scopes and PKCE
    /// challenge. Returns the redirect with <c>code</c> and <c>state</c>.
    /// </summary>
    public static async Task<string> IssueCodeAsync(KbDbContext db, OAuthAuthorizeRequest request, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var grant = await db.OAuthGrants.FirstOrDefaultAsync(g => g.UserId == userId && g.ClientId == request.ClientId, ct);
        if (grant is null)
        {
            grant = new OAuthGrant { UserId = userId, ClientId = request.ClientId, Scopes = request.Scopes, CreatedAt = now, LastUsedAt = now };
            db.OAuthGrants.Add(grant);
        }
        else
        {
            grant.Scopes = OAuthScopes.Format(OAuthScopes.Parse(grant.Scopes + " " + request.Scopes) ?? []);
            grant.LastUsedAt = now;
        }
        var settings = await CompatAuthSettings.LoadAsync(db, ct);
        var code = OAuthSecrets.NewCode();
        db.OAuthCodes.Add(new OAuthCode
        {
            Hash = OAuthSecrets.Hash(code), GrantId = grant.Id, RedirectUri = request.RedirectUri, Scopes = request.Scopes, CodeChallenge = request.CodeChallenge,
            CreatedAt = now, ExpiresAt = now.AddSeconds(settings.CodeSeconds),
        });
        await db.SaveChangesAsync(ct);
        return Append(request.RedirectUri, ("code", code), ("state", request.State));
    }

    /// <summary>The request if it is still open to this user: undecided, unexpired, its client enabled, and not bound to someone else.</summary>
    public static async Task<(OAuthAuthorizeRequest Request, OAuthClient Client)?> OpenAsync(KbDbContext db, Guid id, Guid me, DateTimeOffset now, CancellationToken ct)
    {
        var row = await (from r in db.OAuthAuthorizeRequests
                         where r.Id == id && r.Decision == null && r.ExpiresAt > now && (r.UserId == null || r.UserId == me)
                         join c in db.OAuthClients on r.ClientId equals c.Id
                         where c.Enabled
                         select new { r, c }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.r, row.c);
    }

    public static string ErrorPage(string message) =>
        $"""<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Authorization failed</title></head><body><h1>This application cannot be authorized</h1><p>{System.Net.WebUtility.HtmlEncode(message)}</p><p>Nothing was sent to the application. Tell whoever runs it.</p></body></html>""";
}

internal sealed class DcOAuthAuthorizeHandler(KbDbContext db, TimeProvider time, Microsoft.AspNetCore.Identity.UserManager<KbUser> users) : IActionHandler<DcOAuthAuthorize, OAuthAuthorizeResult>
{
    // Anyone may start: the client and redirect URI are the check, and the user signs in on the way.
    public Task<Problem?> AuthorizeAsync(ActionContext<DcOAuthAuthorize> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<OAuthAuthorizeResult>> HandleAsync(ActionContext<DcOAuthAuthorize> ctx)
    {
        var q = ctx.Action.Query;
        var repeated = q.Any(kv => kv.Value.Length > 1);
        string? One(string name) => q.TryGetValue(name, out var v) && v.Length > 0 ? v[0] : null;

        // Client and redirect URI first: if either is bad, an error page and never a redirect (RFC 6749 section 4.1.2.1).
        var clientId = One("client_id");
        var uri = One("redirect_uri");
        var client = clientId is null ? null : await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ctx.Cancel);
        if (client is null || !client.Enabled) return new OAuthAuthorizeResult(null, OAuthFlow.ErrorPage("The application is not registered here, or has been disabled."));
        if (q.TryGetValue("client_id", out var ids) && ids.Length > 1 || q.TryGetValue("redirect_uri", out var uris) && uris.Length > 1 || uri is null
            || !OAuthClientRules.Uris(client).Any(r => OAuthRedirects.Matches(r, uri)))
            return new OAuthAuthorizeResult(null, OAuthFlow.ErrorPage("The redirect URI is not one registered for this application."));

        var state = One("state");
        OAuthAuthorizeResult Error(string code, string description) => new(OAuthFlow.Append(uri, ("error", code), ("error_description", description), ("state", state)), null);

        if (repeated) return Error("invalid_request", "A parameter was sent more than once.");
        if (One("response_type") != "code") return Error("unsupported_response_type", "Only response_type=code is offered.");
        if (state is { Length: > 2000 }) return Error("invalid_request", "state is too long.");
        var asked = OAuthScopes.Parse(One("scope"));
        var allowed = client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (asked is not { Count: > 0 } || !asked.All(allowed.Contains)) return Error("invalid_scope", "Ask for one or more of the scopes this application is registered with.");
        var challenge = One("code_challenge");
        var method = One("code_challenge_method");
        if (challenge is null && (client.IsPublic || client.RequirePkce)) return Error("invalid_request", "This application must send code_challenge (PKCE, S256).");
        if (challenge is not null && (method is not ("S256" or "sha256") || !OAuthFlow.PkceValue().IsMatch(challenge)))
            return Error("invalid_request", "code_challenge_method must be S256, and code_challenge 43 to 128 characters of [A-Za-z0-9-._~]; plain is not accepted.");

        var now = time.GetUtcNow();
        var me = ctx.Actor.UserId is { } id && await users.FindByIdAsync(id.ToString()) is { DeactivatedAt: null } ? id : (Guid?)null;
        var request = new OAuthAuthorizeRequest
        {
            ClientId = client.Id, UserId = me, RedirectUri = uri, Scopes = OAuthScopes.Format(asked), State = state, CodeChallenge = challenge,
            CreatedAt = now, ExpiresAt = now.Add(OAuthFlow.RequestLife),
        };
        db.OAuthAuthorizeRequests.Add(request);
        await db.SaveChangesAsync(ctx.Cancel);

        // Consent is remembered: a signed-in user whose grant already covers the scopes is sent straight back (identity.md, Consent).
        if (me is { } user && await db.OAuthGrants.AsNoTracking().FirstOrDefaultAsync(g => g.UserId == user && g.ClientId == client.Id, ctx.Cancel) is { } grant
            && asked.All(OAuthScopes.Parse(grant.Scopes)!.Contains))
        {
            request.Decision = "allow";
            request.DecidedAt = now;
            var location = await OAuthFlow.IssueCodeAsync(db, request, user, now, ctx.Cancel);
            ctx.Audit(AuditActions.OAuthConsentGrant, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string>
                { ["clientId"] = client.ClientId, ["scopes"] = request.Scopes, ["remembered"] = "true", ["ip"] = ctx.Actor.RemoteIp ?? "" });
            return new OAuthAuthorizeResult(location, null);
        }
        return new OAuthAuthorizeResult("/oauth/consent/" + request.Id.ToString("D"), null);
    }
}

internal sealed class ReadOAuthAuthorizeRequestHandler(KbDbContext db, TimeProvider time) : IActionHandler<ReadOAuthAuthorizeRequest, OAuthAuthorizeRequestDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadOAuthAuthorizeRequest> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<OAuthAuthorizeRequestDto>> HandleAsync(ActionContext<ReadOAuthAuthorizeRequest> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        if (await OAuthFlow.OpenAsync(db, ctx.Action.Id, me, time.GetUtcNow(), ctx.Cancel) is not { } open) return Problem.NotFound;
        var (request, client) = open;
        var granted = await db.OAuthGrants.Where(g => g.UserId == me && g.ClientId == client.Id).Select(g => g.Scopes).FirstOrDefaultAsync(ctx.Cancel) ?? "";
        return new OAuthAuthorizeRequestDto(request.Id, client.Name, request.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), granted.Split(' ', StringSplitOptions.RemoveEmptyEntries), request.ExpiresAt);
    }
}

internal sealed class DecideOAuthAuthorizeRequestHandler(KbDbContext db, TimeProvider time) : IActionHandler<DecideOAuthAuthorizeRequest, OAuthDecisionDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<DecideOAuthAuthorizeRequest> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<OAuthDecisionDto>> HandleAsync(ActionContext<DecideOAuthAuthorizeRequest> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        var now = time.GetUtcNow();
        if (await OAuthFlow.OpenAsync(db, ctx.Action.Id, me, now, ctx.Cancel) is not { } open) return Problem.NotFound;
        var (request, client) = open;
        var decision = ctx.Action.Allow ? "allow" : "deny";
        // Decided once: of two decisions racing, only the first changes the row.
        var won = await db.OAuthAuthorizeRequests.Where(r => r.Id == request.Id && r.Decision == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Decision, decision).SetProperty(r => r.DecidedAt, now).SetProperty(r => r.UserId, me), ctx.Cancel);
        if (won != 1) return Problem.NotFound;
        var data = new Dictionary<string, string> { ["clientId"] = client.ClientId, ["scopes"] = request.Scopes, ["ip"] = ctx.Actor.RemoteIp ?? "" };
        if (!ctx.Action.Allow)
        {
            ctx.Audit(AuditActions.OAuthConsentDeny, "oauth.client", client.Id.ToString(), data: data);
            return new OAuthDecisionDto(OAuthFlow.Append(request.RedirectUri, ("error", "access_denied"), ("error_description", "The user denied the request."), ("state", request.State)));
        }
        var location = await OAuthFlow.IssueCodeAsync(db, request, me, now, ctx.Cancel);
        ctx.Audit(AuditActions.OAuthConsentGrant, "oauth.client", client.Id.ToString(), data: data);
        return new OAuthDecisionDto(location);
    }
}
