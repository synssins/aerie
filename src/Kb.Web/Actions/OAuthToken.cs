using System.Text.Json.Nodes;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>An answer of the token or revoke endpoint: its status, its JSON (RFC 6749 section 5), and the headers it needs.</summary>
public sealed record OAuthEndpointResult(int Status, JsonObject? Body, bool BasicChallenge = false, int? RetryAfter = null);

/// <summary>
/// <c>POST /rest/oauth2/latest/token</c> (identity.md, The token endpoint): exchanges an authorization code, or a refresh token, for tokens.
/// Parameters come from the form body and the query string, the body winning; a confidential client authenticates with parameters or Basic.
/// </summary>
[Action("oauth.token")]
public sealed record DcOAuthToken : IAction<OAuthEndpointResult>
{
    /// <summary>Every parameter with every value it was sent with, body values replacing query values of the same name.</summary>
    public required IReadOnlyDictionary<string, string[]> Parameters { get; init; }
    /// <summary>Client id and secret from an Authorization: Basic header, or null when none was sent.</summary>
    public (string Id, string Secret)? Basic { get; init; }
    /// <summary>Set when the Authorization header was Basic but could not be read.</summary>
    public bool BadBasic { get; init; }
    public string Address { get; init; } = "";
}

/// <summary>What the token and revoke endpoints share: parameters, client authentication, errors and the limit per client and per address.</summary>
internal static class OAuthEndpoint
{
    private static string? _dummyHash;

    public static OAuthEndpointResult Error(int status, string code, string description, bool basic = false) =>
        new(status, new JsonObject { ["error"] = code, ["error_description"] = description }, basic && status == 401);

    public static OAuthEndpointResult Limited(int retryAfter) =>
        new(429, new JsonObject { ["error"] = "temporarily_unavailable", ["error_description"] = "Too many requests; try again later." }, RetryAfter: retryAfter);

    /// <summary>
    /// The client the request authenticates as, or the error. Unknown, disabled, wrong secret and a public client sending a secret are all
    /// invalid_client, after the same password-hash work, so the answer's time does not tell which. Using parameters and Basic with different
    /// values is invalid_request.
    /// </summary>
    public static async Task<(OAuthClient? Client, OAuthEndpointResult? Error)> AuthenticateAsync(KbDbContext db, IPasswordHasher<KbUser> hasher,
        IReadOnlyDictionary<string, string[]> p, (string Id, string Secret)? basic, bool badBasic, CancellationToken ct)
    {
        var usedBasic = basic is not null || badBasic;
        if (badBasic) return (null, Error(401, "invalid_client", "The Basic credentials could not be read.", basic: true));
        var id = One(p, "client_id");
        var secret = One(p, "client_secret");
        if (basic is { } b)
        {
            if (id is not null && id != b.Id || secret is not null && secret != b.Secret) return (null, Error(400, "invalid_request", "The client is named twice, differently."));
            (id, secret) = (b.Id, b.Secret);
        }
        if (string.IsNullOrEmpty(id)) return (null, Error(401, "invalid_client", "Name the client: client_id, or HTTP Basic.", usedBasic));
        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == id, ct);
        _dummyHash ??= hasher.HashPassword(new KbUser(), OAuthSecrets.NewClientSecret());
        var ok = client is { Enabled: true } && (client.IsPublic
            ? string.IsNullOrEmpty(secret)
            : !string.IsNullOrEmpty(secret) && hasher.VerifyHashedPassword(new KbUser(), client.SecretHash!, secret) != PasswordVerificationResult.Failed);
        if (client is null || client.IsPublic || string.IsNullOrEmpty(secret)) _ = hasher.VerifyHashedPassword(new KbUser(), _dummyHash, secret ?? "");
        return ok ? (client, null) : (null, Error(401, "invalid_client", "The client is unknown, disabled, or its credentials are wrong.", usedBasic));
    }

    public static string? One(IReadOnlyDictionary<string, string[]> p, string name) => p.TryGetValue(name, out var v) && v.Length > 0 ? v[0] : null;

    public static JsonObject Tokens(string access, string refresh, int expiresIn, DateTimeOffset now, string scopes) => new()
    {
        ["access_token"] = access, ["token_type"] = "bearer", ["expires_in"] = expiresIn, ["refresh_token"] = refresh,
        ["created_at"] = now.ToUnixTimeSeconds(), ["scope"] = scopes,
    };

    /// <summary>A new access and refresh pair in a family. Only the hashes are stored; the texts are returned once.</summary>
    public static (string Access, string Refresh) NewPair(KbDbContext db, Guid familyId, string scopes, CompatAuthSettings settings, DateTimeOffset now)
    {
        var (access, refresh) = (OAuthSecrets.NewAccessToken(), OAuthSecrets.NewRefreshToken());
        var token = new OAuthAccessToken { Hash = OAuthSecrets.Hash(access), Prefix = access[..OAuthSecrets.PrefixShown], FamilyId = familyId, Scopes = scopes, CreatedAt = now, ExpiresAt = now.AddSeconds(settings.AccessTokenSeconds) };
        db.OAuthAccessTokens.Add(token);
        db.OAuthRefreshTokens.Add(new OAuthRefreshToken { Hash = OAuthSecrets.Hash(refresh), Prefix = refresh[..OAuthSecrets.PrefixShown], FamilyId = familyId, AccessTokenId = token.Id, CreatedAt = now, ExpiresAt = now.AddDays(settings.RefreshTokenDays) });
        return (access, refresh);
    }

    /// <summary>Revokes a family: every token issued in it stops working at once. The consent stays.</summary>
    public static Task<int> RevokeFamilyAsync(KbDbContext db, Guid familyId, string reason, DateTimeOffset now, CancellationToken ct) =>
        db.OAuthTokenFamilies.Where(f => f.Id == familyId && f.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.RevokedAt, now).SetProperty(f => f.RevokeReason, reason), ct);
}

/// <summary>Requests to the token and revoke endpoints a minute, per client and per address, on one server (identity-plan.md, slices 6 and 10).</summary>
public sealed class OAuthThrottle(Microsoft.Extensions.Options.IOptions<Kb.Core.Config.KbOptions> options, TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Start, int Count)> _hits = new(StringComparer.Ordinal);

    /// <summary>Counts a request; seconds to wait when the key is past its limit, else null.</summary>
    public int? Hit(string key)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var e = _hits.AddOrUpdate(key, (now, 1), (_, e) => now - e.Start < 60 ? (e.Start, e.Count + 1) : (now, 1));
        // shortcut: counted in memory on each server, like aerie's other limits; ended windows are dropped once many keys are held.
        if (_hits.Count > 10_000) foreach (var (k, v) in _hits) if (now - v.Start >= 60) _hits.TryRemove(k, out _);
        return e.Count > options.Value.Auth.OAuthTokenRequestsPerMinute ? (int)Math.Max(1, e.Start + 60 - now) : null;
    }
}

internal sealed class DcOAuthTokenHandler(KbDbContext db, IPasswordHasher<KbUser> hasher, TimeProvider time, OAuthThrottle throttle) : IActionHandler<DcOAuthToken, OAuthEndpointResult>
{
    // The client authenticates in the handler; there is no signed-in caller to permit.
    public Task<Problem?> AuthorizeAsync(ActionContext<DcOAuthToken> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<OAuthEndpointResult>> HandleAsync(ActionContext<DcOAuthToken> ctx)
    {
        var a = ctx.Action;
        var p = a.Parameters;
        // Limited before any credential is checked.
        if (throttle.Hit("address:" + a.Address) is { } wait) return OAuthEndpoint.Limited(wait);
        if ((a.Basic?.Id ?? OAuthEndpoint.One(p, "client_id")) is { } named && throttle.Hit("client:" + named) is { } clientWait) return OAuthEndpoint.Limited(clientWait);
        if (p.Any(kv => kv.Value.Length > 1)) return OAuthEndpoint.Error(400, "invalid_request", "A parameter was sent more than once.");
        var grantType = OAuthEndpoint.One(p, "grant_type");
        if (string.IsNullOrEmpty(grantType)) return OAuthEndpoint.Error(400, "invalid_request", "grant_type is missing.");

        var (client, error) = await OAuthEndpoint.AuthenticateAsync(db, hasher, p, a.Basic, a.BadBasic, ctx.Cancel);
        if (error is not null) return error;
        return grantType switch
        {
            "authorization_code" => await ExchangeAsync(ctx, client!),
            _ => OAuthEndpoint.Error(400, "unsupported_grant_type", "Only authorization_code is offered."),
        };
    }

    /// <summary>
    /// A code for tokens (RFC 6749 section 4.1.3, RFC 7636 section 4.6). Single use: a second exchange is invalid_grant and revokes the family
    /// the first one started. The code must be this client's, unexpired, sent with the same redirect URI, and its PKCE verifier must match.
    /// </summary>
    private async Task<OAuthEndpointResult> ExchangeAsync(ActionContext<DcOAuthToken> ctx, OAuthClient client)
    {
        var p = ctx.Action.Parameters;
        var text = OAuthEndpoint.One(p, "code");
        var redirect = OAuthEndpoint.One(p, "redirect_uri");
        var verifier = OAuthEndpoint.One(p, "code_verifier");
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(redirect)) return OAuthEndpoint.Error(400, "invalid_request", "code and redirect_uri are needed.");
        var now = time.GetUtcNow();
        var hash = OAuthSecrets.Hash(text);
        var row = await (from c in db.OAuthCodes.AsNoTracking()
                         where c.Hash == hash
                         join g in db.OAuthGrants on c.GrantId equals g.Id
                         join u in db.Users on g.UserId equals u.Id
                         select new { c, g, u.DeactivatedAt }).FirstOrDefaultAsync(ctx.Cancel);
        OAuthEndpointResult Bad(string why) => OAuthEndpoint.Error(400, "invalid_grant", why);
        if (row is null || row.g.ClientId != client.Id) return Bad("The code is unknown, or not this client's.");
        var code = row.c;
        if (code.UsedAt is not null) return await ReuseAsync(ctx, client, code, now);
        if (code.ExpiresAt <= now) return Bad("The code has expired.");
        if (code.RedirectUri != redirect) return Bad("redirect_uri is not the one the code was issued for.");
        if (code.CodeChallenge is { } challenge)
        {
            if (verifier is null || !OAuthFlow.PkceValue().IsMatch(verifier)) return OAuthEndpoint.Error(400, "invalid_request", "code_verifier is missing or malformed.");
            if (!OAuthSecrets.FixedEquals(OAuthSecrets.S256(verifier), challenge)) return Bad("code_verifier does not match the code challenge.");
        }
        if (row.DeactivatedAt is not null) return Bad("The user has been disabled.");
        var settings = await CompatAuthSettings.LoadAsync(db, ctx.Cancel);
        if (settings.GrantMaxDays is { } days && row.g.CreatedAt.AddDays(days) <= now) return Bad("The consent has expired; authorize again.");

        // The family exists before the code points at it, so a racing second exchange always finds the family it must revoke.
        var family = new OAuthTokenFamily { GrantId = row.g.Id, Scopes = code.Scopes, CreatedAt = now, LastUsedAt = now };
        db.OAuthTokenFamilies.Add(family);
        await db.SaveChangesAsync(ctx.Cancel);
        var won = await db.OAuthCodes.Where(c => c.Id == code.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now).SetProperty(c => c.FamilyId, family.Id), ctx.Cancel);
        if (won != 1)
        {
            await db.OAuthTokenFamilies.Where(f => f.Id == family.Id).ExecuteDeleteAsync(ctx.Cancel);
            return await ReuseAsync(ctx, client, await db.OAuthCodes.AsNoTracking().SingleAsync(c => c.Id == code.Id, ctx.Cancel), now);
        }
        var (access, refresh) = OAuthEndpoint.NewPair(db, family.Id, code.Scopes, settings, now);
        await db.OAuthGrants.Where(g => g.Id == row.g.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now), ctx.Cancel);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.OAuthTokenIssue, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string>
            { ["clientId"] = client.ClientId, ["user"] = row.g.UserId.ToString(), ["family"] = family.Id.ToString(), ["prefix"] = access[..OAuthSecrets.PrefixShown], ["scopes"] = code.Scopes, ["ip"] = ctx.Action.Address });
        return new OAuthEndpointResult(200, OAuthEndpoint.Tokens(access, refresh, settings.AccessTokenSeconds, now, code.Scopes));
    }

    /// <summary>A used code presented again: the tokens issued from it are revoked (RFC 6749 section 4.1.2), and the event audited.</summary>
    private async Task<OAuthEndpointResult> ReuseAsync(ActionContext<DcOAuthToken> ctx, OAuthClient client, OAuthCode code, DateTimeOffset now)
    {
        if (code.FamilyId is { } family) await OAuthEndpoint.RevokeFamilyAsync(db, family, "code reused", now, ctx.Cancel);
        ctx.Audit(AuditActions.OAuthTokenReuse, "oauth.client", client.Id.ToString(), data: new Dictionary<string, string>
            { ["clientId"] = client.ClientId, ["kind"] = "code", ["family"] = code.FamilyId?.ToString() ?? "", ["ip"] = ctx.Action.Address });
        return OAuthEndpoint.Error(400, "invalid_grant", "The code was already used; the tokens issued from it are revoked.");
    }
}
