using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web;

/// <summary>
/// API tokens: <c>Authorization: Bearer kbt_...</c>. A request with a token acts as the token's owner, narrowed by the token's scopes.
/// It never has Admin power, and it reaches only the routes listed in <see cref="Open"/>; every other route answers 403.
/// </summary>
public static class ApiTokenAuth
{
    public const string Scheme = "KbToken";
    /// <summary>The default scheme: it sends a request with a Bearer header to the token handler and every other request to the sign-in cookie.</summary>
    public const string Selector = "Kb";
    public const string MaxDaysKey = "Tokens.MaxDays";
    public const int DefaultMaxDays = 365;
    private const string TokenItem = "kb.token", RefusedItem = "kb.token.refused";

    /// <summary>
    /// The routes a token may call, each of which decides access through <see cref="ISpacePermissionService"/>.
    /// A route that is not here is closed to tokens, so a new route is closed until someone lists it.
    /// </summary>
    /// <summary>True when a token may call this route (method and route template). For the API reference; the middleware below decides.</summary>
    internal static bool IsOpen(string method, string template) => Open.Contains($"{method} {template}");

    private static readonly HashSet<string> Open = new(StringComparer.Ordinal)
    {
        "GET /api/v1/me", "GET /api/v1/spaces", "GET /api/v1/spaces/{id:guid}", "GET /api/v1/spaces/{id:guid}/tree", "GET /api/v1/home", "GET /api/v1/search",
        "GET /api/v1/pages/{id:guid}", "GET /api/v1/pages/{id:guid}/diff", "GET /api/v1/pages/{id:guid}/markdown", "GET /api/v1/pages/{id:guid}/versions",
        "GET /api/v1/pages/{id:guid}/versions/{version}", "GET /api/v1/pages/{id:guid}/export", "GET /api/v1/pages/{id:guid}/export/code/{n:int}",
        "GET /api/v1/pages/{id:guid}/export/table/{n:int}", "GET /api/v1/images/{id:guid}", "GET /api/v1/attachments/{id:guid}",
        "GET /api/v1/brand", "GET /api/v1/brand/logo/{variant}",
        "POST /api/v1/spaces/{id:guid}/pages", "PUT /api/v1/pages/{id:guid}", "POST /api/v1/pages/{id:guid}/move", "DELETE /api/v1/pages/{id:guid}",
        "POST /api/v1/pages/{id:guid}/versions/{version}/restore", "POST /api/v1/pages/{id:guid}/attachments", "DELETE /api/v1/attachments/{id:guid}",
        "POST /api/v1/spaces/{id:guid}/images", "POST /api/v1/spaces/{id:guid}/images/import",
        // A CMDB or scan feed keeps the inventory up to date; its owner must be one the Admin lets edit the inventory.
        "POST /api/v1/inventory/sync",
    };

    /// <summary>The token this request was authenticated with, or null for a signed-in person.</summary>
    public static ApiToken? Current(HttpContext? http) => http?.Items[TokenItem] as ApiToken;

    internal static void Accept(HttpContext http, ApiToken token) => http.Items[TokenItem] = token;
    internal static void Refuse(HttpContext http, ApiToken token, string reason) => http.Items[RefusedItem] = (token, reason);
    private static (ApiToken Token, string Reason)? Refused(HttpContext http) => http.Items[RefusedItem] as (ApiToken, string)?;

    public static bool HasBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The token a request presented, accepted or not: what the rate limit counts by.</summary>
    public static Guid? PresentedId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Current(http)?.Id ?? Refused(http)?.Token.Id;
    }

    /// <summary>
    /// Runs after authentication and the rate limit. Every use of a token is audited before the request is served,
    /// so a use that cannot be recorded does not happen. Then the route and write rules are applied.
    /// </summary>
    public static async Task EnforceAsync(HttpContext http, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
        if (Refused(http) is { } refused)
        {
            // Only a token that exists is recorded. One that never existed is not, so the audit log cannot be flooded with guesses.
            // On the compatible surface the refusal has the name identity.md gives it; the token is named by id and prefix only.
            var action = Kb.Web.DataCenter.DcRest.IsUnderRoot(http) ? AuditActions.TokenUseDenied : AuditActions.TokenRefused;
            await http.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(action, refused.Token.OwnerUserId, "token", refused.Token.Id.ToString(),
                Data: new Dictionary<string, string> { ["reason"] = refused.Reason, ["ip"] = ip, ["prefix"] = refused.Token.Prefix }), http.RequestAborted);
        }
        if (HasBearer(http.Request) && Current(http) is null)
        {
            // A token that was presented and refused is never served as someone not signed in, even on a route open to them.
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.ChallengeAsync(http, Scheme);
            return;
        }
        if (Current(http) is { } token)
        {
            var method = http.Request.Method;
            var path = http.Request.Path.Value ?? "";
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? path[..Math.Min(path.Length, 200)];
            var services = http.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            await services.GetRequiredService<KbDbContext>().ApiTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), http.RequestAborted);
            // shortcut: one audit entry per call, appended under the audit chain's lock; batch them per token if call volume makes the lock a bottleneck.
            await services.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.TokenUse, token.OwnerUserId, "token", token.Id.ToString(),
                Data: new Dictionary<string, string> { ["method"] = method, ["route"] = route, ["ip"] = ip, [AuditData.TokenKey] = token.Name, [AuditData.TokenIdKey] = token.Id.ToString("D") }), http.RequestAborted);

            var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
            // Every route of the Data Center compatible surface decides access through the permission service, so all of them are open to tokens.
            var dc = http.GetEndpoint()?.Metadata.GetMetadata<Kb.Web.DataCenter.DcRoute>() is not null || http.GetEndpoint()?.Metadata.GetMetadata<Kb.Web.DataCenter.DcDownloadRoute>() is not null;
            var open = dc || Open.Contains($"{method} {route}");
            if (!open || (!read && !token.Scopes().Any(s => s.Access == TokenAccess.Write)))
            {
                // Says which: the route is not one for tokens, or this one needs a Write scope.
                var message = open ? "This token needs the Write scope." : "API tokens can't call this route.";
                await (dc ? Kb.Web.DataCenter.DcRest.Fail(403, message) : Results.Problem(message, statusCode: StatusCodes.Status403Forbidden)).ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }

    /// <summary>
    /// Checks a token's text: it must exist, be neither revoked nor expired, and its owner active. On success the request acts as the owner,
    /// narrowed by the token, exactly as with Bearer. Used by the Bearer handler and by Basic with a token in the password field.
    /// </summary>
    /// <param name="ownerName">For Basic: the user name sent with the token, which must be the owner's (identity.md, HTTP Basic).</param>
    internal static async Task<AuthenticateResult> AuthenticateAsync(HttpContext http, KbDbContext db, TimeProvider clock, string text, string scheme, string? ownerName = null)
    {
        if (!TokenFormat.IsWellFormed(text)) return AuthenticateResult.Fail("Not a token.");
        var hash = TokenFormat.Hash(text);
        var token = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Hash == hash, http.RequestAborted);
        if (token is null) return AuthenticateResult.Fail("Unknown token.");

        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == token.OwnerUserId, http.RequestAborted);
        var reason = token.RevokedAt is not null ? "revoked"
            : token.ExpiresAt <= clock.GetUtcNow() ? "expired"
            : owner is null || owner.DeactivatedAt is not null ? "owner deactivated"
            : ownerName is not null && !string.Equals(ownerName, owner.UserName, StringComparison.OrdinalIgnoreCase) ? "user name is not the owner's"
            : null;
        if (reason is not null)
        {
            Refuse(http, token, reason);
            return AuthenticateResult.Fail("Token " + reason + ".");
        }
        Accept(http, token);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, token.OwnerUserId.ToString()), new Claim("kb:token", token.Id.ToString())], scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), scheme));
    }

    public static async Task<int> MaxDaysAsync(KbDbContext db, CancellationToken ct = default) =>
        int.TryParse(await db.GetAsync(MaxDaysKey, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650 ? days : DefaultMaxDays;
}

public sealed class ApiTokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, KbDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Looks the token up by its hash on every request, with nothing kept between requests, so a revoke holds on every server at once.</summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiTokenAuth.HasBearer(Request)) return AuthenticateResult.NoResult();
        var text = Request.Headers.Authorization.ToString()[7..].Trim();
        return await ApiTokenAuth.AuthenticateAsync(Context, db, clock, text, ApiTokenAuth.Scheme);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // On the compatible surface, RFC 6750 section 3 (identity.md, Errors on protected calls).
        Response.Headers.WWWAuthenticate = Kb.Web.DataCenter.DcRest.IsUnderRoot(Context) ? "Bearer error=\"invalid_token\"" : "Bearer";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The permission service every route uses. For a signed-in person it is the plain service.
/// For a token it narrows the answer: no scope for a space means no permission there; Read caps it at Read, and Write at Read, Create and
/// edit, and Move and delete own (<see cref="SpacePerms.WriteToken"/>).
/// A service token has no person behind it, so its role is what its scopes say, and a scope for every space never reaches a sensitive one.
/// </summary>
public sealed class TokenScopedPermissions(SpacePermissionService inner, ISpaceAccessData data, IHttpContextAccessor http) : ISpacePermissionService
{
    private ApiToken? TokenOf(Guid userId) => ApiTokenAuth.Current(http.HttpContext) is { } t && t.OwnerUserId == userId ? t : null;

    private static int Cap(TokenAccess access) => access == TokenAccess.Write ? SpacePerms.WriteToken : SpacePerms.Read;

    /// <summary>
    /// On the Data Center compatible surface a personal token's role decides and its scope only filters (contracts/behaviour/identity.md,
    /// Scopes): Write admits every write the owner's role holds, Read what a Reader could do. Aerie's own API keeps its narrower cap.
    /// </summary>
    private int PersonalCap(TokenAccess access) =>
        access == TokenAccess.Write && Kb.Web.DataCenter.DcRest.IsSurfaceRoute(http.HttpContext) ? SpacePerms.All : Cap(access);

    public async Task<int> GetPermissionsAsync(Guid userId, Guid spaceId, CancellationToken ct = default)
    {
        if (TokenOf(userId) is not { } token) return await inner.GetPermissionsAsync(userId, spaceId, ct);
        if (token.Kind == TokenKind.Service)
        {
            if (await data.GetSpaceAsync(spaceId, ct) is not { } space) return 0;
            return token.AccessTo(spaceId, includeGlobal: !space.IsSensitive) is { } granted ? Cap(granted) : 0;
        }
        return token.AccessTo(spaceId) is { } access ? await inner.GetPermissionsAsync(userId, spaceId, ct) & PersonalCap(access) : 0;
    }

    public async Task<SpaceRole?> GetRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default) =>
        SpacePerms.Rank(await GetPermissionsAsync(userId, spaceId, ct), await data.GetRoleDefsAsync(ct));

    public async Task<bool> CanAsync(Guid userId, Guid spaceId, SpaceAction action, CancellationToken ct = default) =>
        SpacePerms.Has(await GetPermissionsAsync(userId, spaceId, ct), action);

    public async Task<IReadOnlyList<Guid>> VisibleSpaceIdsAsync(Guid userId, CancellationToken ct = default)
    {
        if (TokenOf(userId) is not { } token) return await inner.VisibleSpaceIdsAsync(userId, ct);
        if (token.Kind == TokenKind.Service)
            return [.. (await data.GetAllSpacesAsync(ct)).Where(s => token.AccessTo(s.Id, includeGlobal: !s.IsSensitive) is not null).Select(s => s.Id)];
        return [.. (await inner.VisibleSpaceIdsAsync(userId, ct)).Where(id => token.AccessTo(id) is not null)];
    }
}
