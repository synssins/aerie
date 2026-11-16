using System.Globalization;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.DataCenter;

/// <summary>
/// The compatible surfaces' sign-in settings (identity.md, Tokens; <c>CompatAuthSettingsDto</c>), kept as runtime settings so every server
/// sees a change at once. Out-of-range values are refused when saved (slice 4) and read back as the default.
/// </summary>
public sealed record CompatAuthSettings(bool BasicPasswordEnabled, bool AllowHttpRedirects, int CodeSeconds, int AccessTokenSeconds, int RefreshTokenDays, int? GrantMaxDays)
{
    public const string AllowHttpRedirectsKey = "Compat.AllowHttpRedirects", CodeSecondsKey = "Compat.CodeSeconds", AccessTokenSecondsKey = "Compat.AccessTokenSeconds",
        RefreshTokenDaysKey = "Compat.RefreshTokenDays", GrantMaxDaysKey = "Compat.GrantMaxDays";

    public static readonly CompatAuthSettings Defaults = new(true, false, 300, 3600, 90, null);

    /// <summary>The first field outside its range (identity.md, Tokens), or null when all are allowed.</summary>
    public string? Invalid() =>
        CodeSeconds is < 60 or > 600 ? "codeSeconds must be 60 to 600."
        : AccessTokenSeconds is < 300 or > 7200 ? "accessTokenSeconds must be 300 to 7200."
        : RefreshTokenDays is < 1 or > 365 ? "refreshTokenDays must be 1 to 365."
        : GrantMaxDays is < 1 or > 730 ? "grantMaxDays must be empty or 1 to 730."
        : null;

    public static async Task<CompatAuthSettings> LoadAsync(KbDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var keys = new[] { RuntimeSettingKeys.CompatBasicPasswordEnabled, AllowHttpRedirectsKey, CodeSecondsKey, AccessTokenSecondsKey, RefreshTokenDaysKey, GrantMaxDaysKey };
        var values = await db.RuntimeSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        int? Int(string key) => values.GetValueOrDefault(key) is { } v && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        var d = Defaults;
        var read = new CompatAuthSettings(values.GetValueOrDefault(RuntimeSettingKeys.CompatBasicPasswordEnabled) != "false", values.GetValueOrDefault(AllowHttpRedirectsKey) == "true",
            Int(CodeSecondsKey) ?? d.CodeSeconds, Int(AccessTokenSecondsKey) ?? d.AccessTokenSeconds, Int(RefreshTokenDaysKey) ?? d.RefreshTokenDays, Int(GrantMaxDaysKey));
        // A value written outside its range (by hand, in the database) falls back to its default, field by field.
        return read with
        {
            CodeSeconds = read.CodeSeconds is >= 60 and <= 600 ? read.CodeSeconds : d.CodeSeconds,
            AccessTokenSeconds = read.AccessTokenSeconds is >= 300 and <= 7200 ? read.AccessTokenSeconds : d.AccessTokenSeconds,
            RefreshTokenDays = read.RefreshTokenDays is >= 1 and <= 365 ? read.RefreshTokenDays : d.RefreshTokenDays,
            GrantMaxDays = read.GrantMaxDays is >= 1 and <= 730 ? read.GrantMaxDays : null,
        };
    }
}

/// <summary>Reading OAuth records the way every check must: a token is live only while its family, grant, client and user all are.</summary>
public static class OAuthStore
{
    /// <summary>
    /// The access token with this text, if it is live: not expired or revoked, its family not revoked, its client enabled, its user active,
    /// and its grant within the absolute limit. Checked on every use, so disabling a user or client, or revoking a grant, takes effect at once.
    /// </summary>
    public static async Task<(OAuthAccessToken Token, OAuthGrant Grant, OAuthClient Client)?> LiveAccessTokenAsync(KbDbContext db, string text, DateTimeOffset now, CancellationToken ct = default) =>
        (await CheckAccessTokenAsync(db, text, now, ct)).Live;

    /// <summary>
    /// As <see cref="LiveAccessTokenAsync"/>, with why a token that exists was refused (expired, revoked, client disabled, user disabled,
    /// consent expired) and its row, for the audit. An unknown token has neither: guesses are not audited.
    /// </summary>
    public static async Task<((OAuthAccessToken Token, OAuthGrant Grant, OAuthClient Client)? Live, OAuthAccessToken? Refused, Guid? User, string? Reason)> CheckAccessTokenAsync(
        KbDbContext db, string text, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!OAuthSecrets.IsAccessToken(text)) return (null, null, null, null);
        var hash = OAuthSecrets.Hash(text);
        var row = await (from t in db.OAuthAccessTokens.AsNoTracking()
                         where t.Hash == hash
                         join f in db.OAuthTokenFamilies on t.FamilyId equals f.Id
                         join g in db.OAuthGrants on f.GrantId equals g.Id
                         join c in db.OAuthClients on g.ClientId equals c.Id
                         join u in db.Users on g.UserId equals u.Id
                         select new { t, f, g, c, u.DeactivatedAt }).FirstOrDefaultAsync(ct);
        if (row is null) return (null, null, null, null);
        var max = (await CompatAuthSettings.LoadAsync(db, ct)).GrantMaxDays;
        var reason = row.t.RevokedAt is not null || row.f.RevokedAt is not null ? "revoked"
            : row.t.ExpiresAt <= now ? "expired"
            : !row.c.Enabled ? "client disabled"
            : row.DeactivatedAt is not null ? "user disabled"
            : max is { } days && row.g.CreatedAt.AddDays(days) <= now ? "consent expired"
            : null;
        return reason is null ? ((row.t, row.g, row.c), null, row.g.UserId, null) : (null, row.t, row.g.UserId, reason);
    }

    /// <summary>
    /// Removes what has expired. A used code and a rotated refresh token stay as long as their family can still be refreshed, because
    /// presenting one again must still revoke the family (identity-plan.md, slice 3). Returns how many rows went.
    /// </summary>
    public static async Task<int> PruneAsync(KbDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var n = await db.OAuthAuthorizeRequests.Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        // A family is over when none of its refresh tokens can be used any more: then its codes and tokens can go with it.
        var over = db.OAuthTokenFamilies.Where(f => !db.OAuthRefreshTokens.Any(r => r.FamilyId == f.Id && r.ExpiresAt > now) && f.CreatedAt < now.AddDays(-1)).Select(f => f.Id);
        n += await db.OAuthCodes.Where(c => c.FamilyId != null && over.Contains(c.FamilyId.Value)).ExecuteDeleteAsync(ct);
        n += await db.OAuthRefreshTokens.Where(r => over.Contains(r.FamilyId)).ExecuteDeleteAsync(ct);
        n += await db.OAuthAccessTokens.Where(t => over.Contains(t.FamilyId)).ExecuteDeleteAsync(ct);
        n += await db.OAuthTokenFamilies.Where(f => !db.OAuthRefreshTokens.Any(r => r.FamilyId == f.Id) && !db.OAuthAccessTokens.Any(t => t.FamilyId == f.Id) && f.CreatedAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        // An unused code past its time can never be exchanged; an expired access token is never accepted (reuse detection needs neither).
        n += await db.OAuthCodes.Where(c => c.UsedAt == null && c.ExpiresAt < now).ExecuteDeleteAsync(ct);
        n += await db.OAuthAccessTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(ct);
        return n;
    }
}

/// <summary>Prunes expired OAuth records every five minutes.</summary>
public sealed class OAuthPruner(IServiceScopeFactory scopes, TimeProvider time, ILogger<OAuthPruner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var n = await OAuthStore.PruneAsync(scope.ServiceProvider.GetRequiredService<KbDbContext>(), time.GetUtcNow(), stoppingToken);
                if (n > 0) log.LogInformation("Removed {Count} expired OAuth records.", n);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "OAuth pruning failed."); }
        }
    }
}
