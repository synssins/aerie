using System.Text.Json.Nodes;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>
/// <c>POST /rest/oauth2/latest/revoke</c> (RFC 7009; identity.md, Revocation). A refresh token ends its whole family, an access token only
/// itself. The answer is 200 whether or not there was anything to revoke, and a token of another client is ignored.
/// </summary>
[Action("oauth.revoke")]
public sealed record DcOAuthRevoke : IAction<OAuthEndpointResult>
{
    public required IReadOnlyDictionary<string, string[]> Parameters { get; init; }
    public (string Id, string Secret)? Basic { get; init; }
    public bool BadBasic { get; init; }
    public string Address { get; init; } = "";
    /// <summary>Whether the site's base URL is https.</summary>
    public bool SiteIsHttps { get; init; } = true;
}

/// <summary>A user's connected application: one consent to one client.</summary>
public sealed record OAuthGrantDto(Guid Id, string ClientName, IReadOnlyList<string> Scopes, DateTimeOffset GrantedAt, DateTimeOffset? LastUsedAt);

/// <summary>The signed-in user's connected applications.</summary>
[Action("oauth.grant.list", Read = true)]
public sealed record ListMyOAuthGrants : IAction<IReadOnlyList<OAuthGrantDto>>;

/// <summary>The signed-in user disconnects an application: the consent and every token issued under it end at once.</summary>
[Action("oauth.grant.revoke")]
public sealed record RevokeMyOAuthGrant : IAction<Unit>
{
    public required Guid Id { get; init; }
}

internal sealed class DcOAuthRevokeHandler(KbDbContext db, IPasswordHasher<KbUser> hasher, TimeProvider time, OAuthThrottle throttle, IHostEnvironment env) : IActionHandler<DcOAuthRevoke, OAuthEndpointResult>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<DcOAuthRevoke> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<OAuthEndpointResult>> HandleAsync(ActionContext<DcOAuthRevoke> ctx)
    {
        var a = ctx.Action;
        var p = a.Parameters;
        if (throttle.Hit("address:" + a.Address) is { } wait) return OAuthEndpoint.Limited(wait);
        if (await OAuthFlow.NeedsHttpsAsync(db, env, a.SiteIsHttps, ctx.Cancel)) return OAuthEndpoint.Error(400, "invalid_request", "OAuth 2.0 works only when this site is served over https.");
        if (p.Any(kv => kv.Value.Length > 1)) return OAuthEndpoint.Error(400, "invalid_request", "A parameter was sent more than once.");
        var (client, error) = await OAuthEndpoint.AuthenticateAsync(db, hasher, p, a.Basic, a.BadBasic, ctx.Cancel);
        if (error is not null) return error;
        if (OAuthEndpoint.ClientLimited(throttle, client!) is { } clientWait) return OAuthEndpoint.Limited(clientWait);
        var text = OAuthEndpoint.One(p, "token");
        if (string.IsNullOrEmpty(text)) return OAuthEndpoint.Error(400, "invalid_request", "token is needed.");
        var now = time.GetUtcNow();
        var hash = OAuthSecrets.Hash(text);
        var mine = db.OAuthGrants.Where(g => g.ClientId == client!.Id).Select(g => g.Id);
        // The hint only says where to look first (RFC 7009 section 2.1); both kinds are looked for.
        var refresh = await db.OAuthRefreshTokens.AsNoTracking()
            .Where(r => r.Hash == hash && db.OAuthTokenFamilies.Any(f => f.Id == r.FamilyId && mine.Contains(f.GrantId))).FirstOrDefaultAsync(ctx.Cancel);
        if (refresh is not null)
        {
            if (await OAuthEndpoint.RevokeFamilyAsync(db, refresh.FamilyId, "revoked by the client", now, ctx.Cancel) > 0)
                ctx.Audit(AuditActions.OAuthGrantRevoke, "oauth.client", client!.Id.ToString(), data: new Dictionary<string, string>
                    { ["clientId"] = client.ClientId, ["by"] = "client", ["kind"] = "refresh", ["family"] = refresh.FamilyId.ToString(), ["prefix"] = refresh.Prefix, ["ip"] = a.Address });
        }
        else if (await db.OAuthAccessTokens.AsNoTracking()
                     .Where(t => t.Hash == hash && db.OAuthTokenFamilies.Any(f => f.Id == t.FamilyId && mine.Contains(f.GrantId))).FirstOrDefaultAsync(ctx.Cancel) is { } access
                 && await db.OAuthAccessTokens.Where(t => t.Id == access.Id && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ctx.Cancel) > 0)
        {
            ctx.Audit(AuditActions.OAuthGrantRevoke, "oauth.client", client!.Id.ToString(), data: new Dictionary<string, string>
                { ["clientId"] = client.ClientId, ["by"] = "client", ["kind"] = "access", ["family"] = access.FamilyId.ToString(), ["prefix"] = access.Prefix, ["ip"] = a.Address });
        }
        return new OAuthEndpointResult(200, null);
    }
}

internal sealed class ListMyOAuthGrantsHandler(KbDbContext db) : IActionHandler<ListMyOAuthGrants, IReadOnlyList<OAuthGrantDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListMyOAuthGrants> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<IReadOnlyList<OAuthGrantDto>>> HandleAsync(ActionContext<ListMyOAuthGrants> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        var rows = await (from g in db.OAuthGrants.AsNoTracking()
                          where g.UserId == me
                          join c in db.OAuthClients on g.ClientId equals c.Id
                          orderby c.Name
                          select new { g, c.Name }).ToListAsync(ctx.Cancel);
        return rows.Select(r => new OAuthGrantDto(r.g.Id, r.Name, r.g.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), r.g.CreatedAt, r.g.LastUsedAt)).ToList();
    }
}

internal sealed class RevokeMyOAuthGrantHandler(KbDbContext db) : IActionHandler<RevokeMyOAuthGrant, Unit>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<RevokeMyOAuthGrant> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RevokeMyOAuthGrant> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        // Only the user's own: anyone else's is not found, an Admin's view included.
        var row = await (from g in db.OAuthGrants.AsNoTracking()
                         where g.Id == ctx.Action.Id && g.UserId == me
                         join c in db.OAuthClients on g.ClientId equals c.Id
                         select new { g.Id, c.ClientId, ClientRow = c.Id }).FirstOrDefaultAsync(ctx.Cancel);
        if (row is null) return Problem.NotFound;
        // The database removes the grant's codes, families and tokens with it: every token ends at once.
        await db.OAuthGrants.Where(g => g.Id == row.Id).ExecuteDeleteAsync(ctx.Cancel);
        ctx.Audit(AuditActions.OAuthGrantRevoke, "oauth.client", row.ClientRow.ToString(), data: new Dictionary<string, string>
            { ["clientId"] = row.ClientId, ["by"] = "user", ["grant"] = row.Id.ToString(), ["ip"] = ctx.Actor.RemoteIp ?? "" });
        return new Unit();
    }
}

/// <summary>The authorization server metadata (RFC 8414; identity.md, Server metadata).</summary>
internal static class OAuthMetadata
{
    public static JsonObject Document(string site, string surface)
    {
        var oauth = surface + DcRest.OAuthRoot + "/latest";
        JsonArray Methods() => ["client_secret_post", "client_secret_basic", "none"];
        return new JsonObject
        {
            ["issuer"] = site,
            ["authorization_endpoint"] = oauth + "/authorize",
            ["token_endpoint"] = oauth + "/token",
            ["revocation_endpoint"] = oauth + "/revoke",
            ["scopes_supported"] = new JsonArray([.. OAuthScopes.All.Select(s => (JsonNode)s)]),
            ["response_types_supported"] = new JsonArray("code"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["token_endpoint_auth_methods_supported"] = Methods(),
            ["revocation_endpoint_auth_methods_supported"] = Methods(),
        };
    }
}
