using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web;

public sealed record CreateTokenRequest(string? Name, TokenKind Kind, IReadOnlyList<TokenScope>? Scopes, int? Days, Guid? AccountId = null);
public sealed record ApiTokenDto(Guid Id, string Name, TokenKind Kind, string Prefix, IReadOnlyList<TokenScope> Scopes, Guid OwnerId, string Owner,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, string State);
/// <summary>The only answer that ever carries a token's text.</summary>
public sealed record ApiTokenCreatedDto(string Token, ApiTokenDto Info);
public sealed record TokenSettingsDto(int MaxDays);

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
            await http.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.TokenRefused, refused.Token.OwnerUserId, "token", refused.Token.Id.ToString(),
                Data: new Dictionary<string, string> { ["reason"] = refused.Reason, ["ip"] = ip }), http.RequestAborted);
        }
        else if (Current(http) is { } token)
        {
            var method = http.Request.Method;
            var path = http.Request.Path.Value ?? "";
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? path[..Math.Min(path.Length, 200)];
            var services = http.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            await services.GetRequiredService<KbDbContext>().ApiTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), http.RequestAborted);
            // shortcut: one audit entry per call, appended under the audit chain's lock; batch them per token if call volume makes the lock a bottleneck.
            await services.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.TokenUse, token.OwnerUserId, "token", token.Id.ToString(),
                Data: new Dictionary<string, string> { ["method"] = method, ["route"] = route, ["ip"] = ip }), http.RequestAborted);

            var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
            if (!Open.Contains($"{method} {route}") || (!read && !token.Scopes().Any(s => s.Access == TokenAccess.Write)))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next(http);
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
        if (!TokenFormat.IsWellFormed(text)) return AuthenticateResult.Fail("Not a token.");
        var hash = TokenFormat.Hash(text);
        var token = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Hash == hash, Context.RequestAborted);
        if (token is null) return AuthenticateResult.Fail("Unknown token.");

        var reason = token.RevokedAt is not null ? "revoked"
            : token.ExpiresAt <= clock.GetUtcNow() ? "expired"
            : !await db.Users.AsNoTracking().AnyAsync(u => u.Id == token.OwnerUserId && u.DeactivatedAt == null, Context.RequestAborted) ? "owner deactivated"
            : null;
        if (reason is not null)
        {
            ApiTokenAuth.Refuse(Context, token, reason);
            return AuthenticateResult.Fail("Token " + reason + ".");
        }
        ApiTokenAuth.Accept(Context, token);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, token.OwnerUserId.ToString()), new Claim("kb:token", token.Id.ToString())], ApiTokenAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiTokenAuth.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
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
/// For a token it narrows the answer: no scope for a space means no role there; Read caps the role at Reader and Write at Contributor.
/// A service token has no person behind it, so its role is what its scopes say, and a scope for every space never reaches a sensitive one.
/// </summary>
public sealed class TokenScopedPermissions(SpacePermissionService inner, ISpaceAccessData data, IHttpContextAccessor http) : ISpacePermissionService
{
    private ApiToken? TokenOf(Guid userId) => ApiTokenAuth.Current(http.HttpContext) is { } t && t.OwnerUserId == userId ? t : null;

    private static SpaceRole Cap(TokenAccess access) => access == TokenAccess.Write ? SpaceRole.Contributor : SpaceRole.Reader;

    public async Task<SpaceRole?> GetRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default)
    {
        if (TokenOf(userId) is not { } token) return await inner.GetRoleAsync(userId, spaceId, ct);
        if (token.Kind == TokenKind.Service)
        {
            if (await data.GetSpaceAsync(spaceId, ct) is not { } space) return null;
            return token.AccessTo(spaceId, includeGlobal: !space.IsSensitive) is { } granted ? Cap(granted) : null;
        }
        if (token.AccessTo(spaceId) is not { } access || await inner.GetRoleAsync(userId, spaceId, ct) is not { } role) return null;
        return role < Cap(access) ? role : Cap(access);
    }

    public async Task<bool> CanAsync(Guid userId, Guid spaceId, SpaceAction action, CancellationToken ct = default) =>
        await GetRoleAsync(userId, spaceId, ct) is { } role && role >= RoleMatrix.MinimumRole(action);

    public async Task<IReadOnlyList<Guid>> VisibleSpaceIdsAsync(Guid userId, CancellationToken ct = default)
    {
        if (TokenOf(userId) is not { } token) return await inner.VisibleSpaceIdsAsync(userId, ct);
        if (token.Kind == TokenKind.Service)
            return [.. (await data.GetAllSpacesAsync(ct)).Where(s => token.AccessTo(s.Id, includeGlobal: !s.IsSensitive) is not null).Select(s => s.Id)];
        return [.. (await inner.VisibleSpaceIdsAsync(userId, ct)).Where(id => token.AccessTo(id) is not null)];
    }
}

public static class TokenRoutes
{
    public static void MapTokenEndpoints(this RouteGroupBuilder api)
    {
        // Personal: anyone, for themselves. Service: Admins only, because its scopes are its access.
        api.MapPost("/tokens", async (CreateTokenRequest req, HttpContext http, KbDbContext db, ISpacePermissionService perms, UserManager<KbUser> users, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var service = req.Kind == TokenKind.Service;
            if (!Enum.IsDefined(req.Kind)) return Invalid("kind", "Personal or Service.");
            if (service && !await db.IsAdminAsync(me)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100) return Invalid("name", "1 to 100 characters.");
            var max = await ApiTokenAuth.MaxDaysAsync(db, ct);
            if (req.Days is not { } days || days < 1 || days > max) return Invalid("days", $"1 to {max}.");
            var scopes = (req.Scopes ?? []).Distinct().ToList();
            if (scopes.Count is 0 or > 50 || scopes.Any(s => !Enum.IsDefined(s.Access))) return Invalid("scopes", "At least one.");
            foreach (var spaceId in scopes.Select(s => s.SpaceId).OfType<Guid>().Distinct())
            {
                // A space the maker cannot see is answered like one that does not exist. A service token is made only by an Owner of the space it names.
                var allowed = service ? await perms.CanAsync(me, spaceId, SpaceAction.ManageRolesSettingsSensitive, ct) : await perms.GetRoleAsync(me, spaceId, ct) is not null;
                if (!allowed) return Invalid("scopes", "Unknown space.");
            }

            var now = time.GetUtcNow();
            var owner = me;
            if (service && req.AccountId is { } accountId)
            {
                if (!await db.Users.AnyAsync(u => u.Id == accountId && u.Source == UserSource.Service && u.DeactivatedAt == null, ct)) return Invalid("accountId", "Unknown account.");
                owner = accountId;
            }
            else if (service)
            {
                // The account has no password, so it cannot sign in. Pages the token changes carry its name.
                var account = new KbUser { UserName = "svc-" + Guid.NewGuid().ToString("N")[..16], DisplayName = name, Source = UserSource.Service, CreatedAt = now, LockoutEnabled = true };
                var made = await users.CreateAsync(account);
                if (!made.Succeeded) return Invalid("name", made.Errors.First().Description);
                owner = account.Id;
            }

            var text = TokenFormat.Generate();
            var token = new ApiToken
            {
                OwnerUserId = owner, Kind = req.Kind, Name = name, Hash = TokenFormat.Hash(text), Prefix = text[..TokenFormat.PrefixShown],
                ScopesJson = System.Text.Json.JsonSerializer.Serialize(scopes), CreatedBy = me, CreatedAt = now, ExpiresAt = now.AddDays(days),
            };
            db.ApiTokens.Add(token);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.TokenCreate, me, "token", token.Id.ToString(), Data: new Dictionary<string, string>
            {
                ["name"] = name, ["kind"] = req.Kind.ToString(), ["days"] = days.ToString(CultureInfo.InvariantCulture),
                ["scopes"] = string.Join(", ", scopes.Select(s => $"{s.SpaceId?.ToString() ?? "all"}:{s.Access}")),
            }), ct);
            return Results.Created($"/api/v1/tokens/{token.Id}", new ApiTokenCreatedDto(text, (await DtosAsync(db, [token], now, ct))[0]));
        });

        api.MapGet("/tokens", async (HttpContext http, KbDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var mine = await db.ApiTokens.AsNoTracking().Where(t => t.OwnerUserId == me && t.Kind == TokenKind.Personal).ToListAsync(ct);
            return Results.Ok(await DtosAsync(db, mine, time.GetUtcNow(), ct));
        });

        // Revoke. 404 for a token that is not the caller's, unless the caller is an Admin.
        api.MapDelete("/tokens/{id:guid}", async (Guid id, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (token is null || !((token.OwnerUserId == me && token.Kind == TokenKind.Personal) || await db.IsAdminAsync(me))) return Results.NotFound();
            if (token.RevokedAt is null)
            {
                token.RevokedAt = time.GetUtcNow();
                await db.SaveChangesAsync(ct);
                await audit.AppendAsync(new AuditEvent(AuditActions.TokenRevoke, me, "token", token.Id.ToString(), Data: new Dictionary<string, string> { ["name"] = token.Name }), ct);
            }
            return Results.NoContent();
        });

        var admin = api.AdminGroup();
        admin.MapGet("/tokens", async (KbDbContext db, TimeProvider time, CancellationToken ct) =>
            Results.Ok(await DtosAsync(db, await db.ApiTokens.AsNoTracking().ToListAsync(ct), time.GetUtcNow(), ct)));

        admin.MapGet("/tokens/settings", async (KbDbContext db, CancellationToken ct) => Results.Ok(new TokenSettingsDto(await ApiTokenAuth.MaxDaysAsync(db, ct))));

        admin.MapPut("/tokens/settings", async (TokenSettingsDto req, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (req.MaxDays is < 1 or > 3650) return Invalid("maxDays", "1 to 3650.");
            var value = req.MaxDays.ToString(CultureInfo.InvariantCulture);
            await db.SetAsync(ApiTokenAuth.MaxDaysKey, value, http.User.UserId(), time.GetUtcNow(), ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.SettingsChange, http.User.UserId(), "setting", ApiTokenAuth.MaxDaysKey, Data: new Dictionary<string, string> { ["value"] = value }), ct);
            return Results.Ok(req);
        });
    }

    private static async Task<List<ApiTokenDto>> DtosAsync(KbDbContext db, IReadOnlyList<ApiToken> tokens, DateTimeOffset now, CancellationToken ct)
    {
        var ownerIds = tokens.Select(t => t.OwnerUserId).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. tokens.OrderByDescending(t => t.CreatedAt).Select(t => new ApiTokenDto(t.Id, t.Name, t.Kind, t.Prefix, t.Scopes(), t.OwnerUserId, owners.GetValueOrDefault(t.OwnerUserId, ""),
            t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt, t.RevokedAt is not null ? "Revoked" : t.ExpiresAt <= now ? "Expired" : "Active"))];
    }

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
