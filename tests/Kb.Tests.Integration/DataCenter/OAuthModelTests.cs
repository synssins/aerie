using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 3: the OAuth records, their values, liveness checks and pruning (identity-plan.md, Slice 3).</summary>
public sealed class OAuthModelTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private sealed record Seed(Guid Client, Guid Grant, Guid Family, Guid User, string Access, string Refresh, Guid Code);

    /// <summary>A client, a user's grant, one family with an access and a refresh token, and the used code that started it.</summary>
    private async Task<Seed> SeedAsync(string user = "ravi")
    {
        var userId = await UserIdAsync(_f, user) ?? await UserAsync(_f, user);
        var (access, refresh) = (OAuthSecrets.NewAccessToken(), OAuthSecrets.NewRefreshToken());
        Seed? seed = null;
        await _f.WithDbAsync(async db =>
        {
            var client = new OAuthClient { ClientId = OAuthSecrets.NewClientId(), Name = "App", Type = "confidential", Scopes = "READ WRITE", CreatedAt = Now };
            var grant = new OAuthGrant { UserId = userId, ClientId = client.Id, Scopes = "READ WRITE", CreatedAt = Now, LastUsedAt = Now };
            var family = new OAuthTokenFamily { GrantId = grant.Id, Scopes = "READ WRITE", CreatedAt = Now, LastUsedAt = Now };
            var token = new OAuthAccessToken { Hash = OAuthSecrets.Hash(access), Prefix = access[..OAuthSecrets.PrefixShown], FamilyId = family.Id, Scopes = "READ WRITE", CreatedAt = Now, ExpiresAt = Now.AddHours(1) };
            var code = new OAuthCode { Hash = OAuthSecrets.Hash(OAuthSecrets.NewCode()), GrantId = grant.Id, RedirectUri = "https://app.example/cb", CreatedAt = Now, ExpiresAt = Now.AddMinutes(5), UsedAt = Now, FamilyId = family.Id };
            db.AddRange(client, grant, family, token, code,
                new OAuthRefreshToken { Hash = OAuthSecrets.Hash(refresh), Prefix = refresh[..OAuthSecrets.PrefixShown], FamilyId = family.Id, AccessTokenId = token.Id, CreatedAt = Now, ExpiresAt = Now.AddDays(90) });
            await db.SaveChangesAsync(Ct);
            seed = new Seed(client.Id, grant.Id, family.Id, userId, access, refresh, code.Id);
        });
        return seed!;
    }

    private async Task<bool> LiveAsync(string access, DateTimeOffset? at = null) =>
        await _f.WithDbValueAsync(db => OAuthStore.LiveAccessTokenAsync(db, access, at ?? Now, Ct)) is not null;

    [Fact]
    public void Values_HaveTheirShapes_AndAreRandom()
    {
        Assert.Matches("^[A-Za-z0-9]{32}$", OAuthSecrets.NewClientId());
        Assert.Matches("^[A-Za-z0-9]{64}$", OAuthSecrets.NewClientSecret());
        Assert.Matches("^[A-Za-z0-9]{43}$", OAuthSecrets.NewCode());
        Assert.Matches("^kbo_[A-Za-z0-9]{43}$", OAuthSecrets.NewAccessToken());
        Assert.Matches("^kbr_[A-Za-z0-9]{43}$", OAuthSecrets.NewRefreshToken());
        Assert.Equal(100, Enumerable.Range(0, 100).Select(_ => OAuthSecrets.NewAccessToken()).Distinct().Count());
        Assert.True(OAuthSecrets.IsAccessToken(OAuthSecrets.NewAccessToken()) && !OAuthSecrets.IsAccessToken(OAuthSecrets.NewRefreshToken()));
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthSecrets.S256("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.True(OAuthSecrets.FixedEquals("abc", "abc"));
        Assert.False(OAuthSecrets.FixedEquals("abc", "abd"));
    }

    [Fact]
    public void Scopes_ParseAndImply()
    {
        Assert.Equal(["READ", "WRITE"], OAuthScopes.Parse("WRITE  READ WRITE"));
        Assert.Null(OAuthScopes.Parse("READ READ_ALL"));
        Assert.Null(OAuthScopes.Parse("read"));
        Assert.Equal(["READ", "WRITE"], OAuthScopes.Expand(["WRITE"]).Order());
        Assert.Equal(["ADMIN", "READ", "SYSTEM_ADMIN", "WRITE"], OAuthScopes.Expand(["SYSTEM_ADMIN"]).Order());
        Assert.Empty(OAuthScopes.Expand([]));
        Assert.Equal("READ ADMIN", OAuthScopes.Format(["ADMIN", "READ"]));
    }

    [Fact]
    public async Task OnlyHashes_AreStored()
    {
        var s = await SeedAsync();
        await _f.WithDbAsync(async db =>
        {
            var token = await db.OAuthAccessTokens.SingleAsync(Ct);
            Assert.Equal(OAuthSecrets.Hash(s.Access), token.Hash);
            Assert.Equal(32, token.Hash.Length);
            Assert.Equal(s.Access[..12], token.Prefix);
        });
    }

    /// <summary>Disabling the user or client, revoking the family or passing the grant's absolute limit each end the token at once.</summary>
    [Fact]
    public async Task AToken_IsLive_OnlyWhileEverythingBehindItIs()
    {
        var s = await SeedAsync();
        Assert.True(await LiveAsync(s.Access));
        Assert.False(await LiveAsync(s.Refresh));
        Assert.False(await LiveAsync(OAuthSecrets.NewAccessToken()));
        Assert.False(await LiveAsync(s.Access, Now.AddHours(2))); // expired

        async Task Toggle(Func<KbDbContext, Task> off, Func<KbDbContext, Task> on)
        {
            await _f.WithDbAsync(off);
            Assert.False(await LiveAsync(s.Access));
            await _f.WithDbAsync(on);
            Assert.True(await LiveAsync(s.Access));
        }
        await Toggle(db => db.OAuthClients.ExecuteUpdateAsync(x => x.SetProperty(c => c.Enabled, false), Ct), db => db.OAuthClients.ExecuteUpdateAsync(x => x.SetProperty(c => c.Enabled, true), Ct));
        await Toggle(db => db.Users.Where(u => u.Id == s.User).ExecuteUpdateAsync(x => x.SetProperty(u => u.DeactivatedAt, Now), Ct),
            db => db.Users.Where(u => u.Id == s.User).ExecuteUpdateAsync(x => x.SetProperty(u => u.DeactivatedAt, (DateTimeOffset?)null), Ct));
        await Toggle(db => db.OAuthTokenFamilies.ExecuteUpdateAsync(x => x.SetProperty(f => f.RevokedAt, Now), Ct),
            db => db.OAuthTokenFamilies.ExecuteUpdateAsync(x => x.SetProperty(f => f.RevokedAt, (DateTimeOffset?)null), Ct));
        await Toggle(db => db.OAuthAccessTokens.ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, Now), Ct),
            db => db.OAuthAccessTokens.ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, (DateTimeOffset?)null), Ct));
        await _f.WithDbAsync(async db =>
        {
            db.RuntimeSettings.Add(new RuntimeSetting { Key = CompatAuthSettings.GrantMaxDaysKey, Value = "1", UpdatedAt = Now });
            await db.SaveChangesAsync(Ct);
        });
        Assert.True(await LiveAsync(s.Access));
        Assert.False(await LiveAsync(s.Access, Now.AddDays(1).AddMinutes(1)));
    }

    [Fact]
    public async Task DeletingAClient_DeletesEverythingIssuedToIt()
    {
        var s = await SeedAsync();
        var other = await SeedAsync("olga");
        await _f.WithDbAsync(async db =>
        {
            db.OAuthAuthorizeRequests.Add(new OAuthAuthorizeRequest { ClientId = s.Client, RedirectUri = "https://app.example/cb", CreatedAt = Now, ExpiresAt = Now.AddMinutes(10) });
            await db.SaveChangesAsync(Ct);
            await db.OAuthClients.Where(c => c.Id == s.Client).ExecuteDeleteAsync(Ct);
            Assert.False(await db.OAuthGrants.AnyAsync(g => g.Id == s.Grant, Ct));
            Assert.False(await db.OAuthTokenFamilies.AnyAsync(f => f.Id == s.Family, Ct));
            Assert.False(await db.OAuthAccessTokens.AnyAsync(t => t.FamilyId == s.Family, Ct));
            Assert.False(await db.OAuthRefreshTokens.AnyAsync(t => t.FamilyId == s.Family, Ct));
            Assert.False(await db.OAuthCodes.AnyAsync(c => c.Id == s.Code, Ct));
            Assert.False(await db.OAuthAuthorizeRequests.AnyAsync(Ct));
            Assert.Equal(1, await db.OAuthAccessTokens.CountAsync(Ct)); // the other client's stays
        });
        Assert.True(await LiveAsync(other.Access));
    }

    /// <summary>Expired records go; a used code and a rotated refresh token stay while their family can still be refreshed.</summary>
    [Fact]
    public async Task Pruning_KeepsWhatReuseDetectionNeeds()
    {
        var s = await SeedAsync();
        await _f.WithDbAsync(async db =>
        {
            db.OAuthAuthorizeRequests.Add(new OAuthAuthorizeRequest { ClientId = s.Client, RedirectUri = "https://app.example/cb", CreatedAt = Now.AddHours(-1), ExpiresAt = Now.AddMinutes(-50) });
            db.OAuthCodes.Add(new OAuthCode { Hash = OAuthSecrets.Hash("unused"), GrantId = s.Grant, RedirectUri = "https://app.example/cb", CreatedAt = Now.AddHours(-1), ExpiresAt = Now.AddMinutes(-55) });
            await db.OAuthRefreshTokens.ExecuteUpdateAsync(x => x.SetProperty(r => r.RotatedAt, Now), Ct);
            await db.SaveChangesAsync(Ct);
        });
        // A day later the access token has expired, the family can still be refreshed (90 days).
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(3, await OAuthStore.PruneAsync(db, Now.AddDays(2), Ct)); // the request, the unused code, the access token
            Assert.True(await db.OAuthCodes.AnyAsync(c => c.Id == s.Code, Ct));
            Assert.True(await db.OAuthRefreshTokens.AnyAsync(Ct));
            Assert.True(await db.OAuthTokenFamilies.AnyAsync(Ct));
        });
        // Once no refresh token of the family can be used, the family goes with its code and tokens; the consent stays.
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(3, await OAuthStore.PruneAsync(db, Now.AddDays(91), Ct));
            Assert.False(await db.OAuthCodes.AnyAsync(Ct));
            Assert.False(await db.OAuthRefreshTokens.AnyAsync(Ct));
            Assert.False(await db.OAuthTokenFamilies.AnyAsync(Ct));
            Assert.True(await db.OAuthGrants.AnyAsync(g => g.Id == s.Grant, Ct));
        });
    }

    [Fact]
    public async Task Settings_HaveDefaults_AndRanges()
    {
        Assert.Equal(CompatAuthSettings.Defaults, await _f.WithDbValueAsync(db => CompatAuthSettings.LoadAsync(db, Ct)));
        Assert.Equal(new CompatAuthSettings(true, false, 300, 3600, 90, null), CompatAuthSettings.Defaults);
        Assert.Null(CompatAuthSettings.Defaults.Invalid());
        Assert.NotNull((CompatAuthSettings.Defaults with { CodeSeconds = 601 }).Invalid());
        Assert.NotNull((CompatAuthSettings.Defaults with { AccessTokenSeconds = 299 }).Invalid());
        Assert.NotNull((CompatAuthSettings.Defaults with { RefreshTokenDays = 0 }).Invalid());
        Assert.NotNull((CompatAuthSettings.Defaults with { GrantMaxDays = 731 }).Invalid());
        Assert.Null((CompatAuthSettings.Defaults with { GrantMaxDays = 730, CodeSeconds = 60, AccessTokenSeconds = 7200, RefreshTokenDays = 365 }).Invalid());
        await _f.WithDbAsync(async db =>
        {
            foreach (var (k, v) in new[] { (CompatAuthSettings.CodeSecondsKey, "9999"), (CompatAuthSettings.AccessTokenSecondsKey, "600"), (CompatAuthSettings.AllowHttpRedirectsKey, "true") })
                db.RuntimeSettings.Add(new RuntimeSetting { Key = k, Value = v, UpdatedAt = Now });
            await db.SaveChangesAsync(Ct);
        });
        var read = await _f.WithDbValueAsync(db => CompatAuthSettings.LoadAsync(db, Ct));
        Assert.Equal((300, 600, true), (read.CodeSeconds, read.AccessTokenSeconds, read.AllowHttpRedirects)); // out of range reads as the default
    }
}
