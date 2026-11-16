using System.Net;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;
using static Kb.Tests.Integration.DataCenter.OAuthKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 8: refresh, rotation and reuse detection (identity.md, Refresh rotation and reuse detection).</summary>
public sealed class OAuthRefreshTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private Task<JsonElement> RefreshAsync(App app, string refresh, HttpStatusCode expected = HttpStatusCode.OK, string? scope = null, string? redirect = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh };
        if (scope is not null) form["scope"] = scope;
        if (redirect is not null) form["redirect_uri"] = redirect;
        return PostTokenAsync(_f, app, form, expected);
    }

    private async Task<bool> LiveAsync(JsonElement tokens) =>
        await _f.WithDbValueAsync(db => OAuthStore.LiveAccessTokenAsync(db, tokens.GetProperty("access_token").GetString()!, DateTimeOffset.UtcNow, Ct)) is not null;

    private static string Refresh(JsonElement tokens) => tokens.GetProperty("refresh_token").GetString()!;

    // auth.oauth.refresh.ok: a new pair; the old access token stops working; the old refresh token too.
    [Fact]
    public async Task Refresh_GivesANewPair_AndEndsTheOldOne()
    {
        var app = await ClientAsync(_admin);
        var first = await TokensAsync(_f, app, "READ WRITE");
        var second = await RefreshAsync(app, Refresh(first), redirect: Cb);
        Assert.NotEqual(Refresh(first), Refresh(second));
        Assert.Equal("READ WRITE", second.GetProperty("scope").GetString());
        Assert.False(await LiveAsync(first));
        Assert.True(await LiveAsync(second));
        Assert.Equal(HttpStatusCode.OK, (await Bearer(_f, second.GetProperty("access_token").GetString()!).GetAsync("/rest/api/user/current", Ct)).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.OAuthTokenFamilies.CountAsync(Ct)); // the same family
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthTokenRefresh, Ct));
        });
        // A third refresh with the second token works on; the chain goes on in one family.
        Assert.True(await LiveAsync(await RefreshAsync(app, Refresh(second))));
    }

    // auth.oauth.refresh.after_access_expiry
    [Fact]
    public async Task Refresh_WorksAfterTheAccessTokenExpired()
    {
        var app = await ClientAsync(_admin);
        var first = await TokensAsync(_f, app);
        await _f.WithDbAsync(db => db.OAuthAccessTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
        Assert.False(await LiveAsync(first));
        Assert.True(await LiveAsync(await RefreshAsync(app, Refresh(first))));
    }

    // auth.oauth.refresh.reuse: the old refresh token again is invalid_grant, and the newest access token stops working too.
    [Fact]
    public async Task ARotatedRefreshToken_PresentedAgain_RevokesTheFamily()
    {
        var app = await ClientAsync(_admin);
        var first = await TokensAsync(_f, app);
        var second = await RefreshAsync(app, Refresh(first));
        Assert.Equal("invalid_grant", (await RefreshAsync(app, Refresh(first), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        Assert.False(await LiveAsync(second));
        Assert.Equal("invalid_grant", (await RefreshAsync(app, Refresh(second), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal("refresh token reused", (await db.OAuthTokenFamilies.SingleAsync(Ct)).RevokeReason);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthTokenReuse && a.DataJson.Contains("refresh"), Ct));
            Assert.True(await db.OAuthGrants.AnyAsync(Ct)); // the consent stays: authorizing again skips the page
        });
        Assert.True(await LiveAsync(await TokensAsync(_f, app)));
    }

    /// <summary>Two refreshes racing with one token: the first wins and the second is reuse, so the family ends.</summary>
    [Fact]
    public async Task TwoRefreshesAtOnce_OneWins_ThenTheFamilyEnds()
    {
        var app = await ClientAsync(_admin);
        var first = await TokensAsync(_f, app);
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = Refresh(first), ["client_id"] = app.ClientId, ["client_secret"] = app.Secret! };
        var both = await Task.WhenAll(_f.NewClient().PostAsync("/rest/oauth2/latest/token", new FormUrlEncodedContent(form), Ct), _f.NewClient().PostAsync("/rest/oauth2/latest/token", new FormUrlEncodedContent(form), Ct));
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.BadRequest);
        await _f.WithDbAsync(async db => Assert.NotNull((await db.OAuthTokenFamilies.SingleAsync(Ct)).RevokedAt));
    }

    // auth.oauth.refresh.narrow_scope
    [Fact]
    public async Task Scope_OnRefresh_OnlyNarrows()
    {
        var app = await ClientAsync(_admin, scopes: ["READ", "WRITE", "ADMIN"]);
        var first = await TokensAsync(_f, app, "READ WRITE");
        Assert.Equal("invalid_scope", (await RefreshAsync(app, Refresh(first), HttpStatusCode.BadRequest, scope: "READ WRITE ADMIN")).GetProperty("error").GetString());
        Assert.Equal("invalid_scope", (await RefreshAsync(app, Refresh(first), HttpStatusCode.BadRequest, scope: "READ_ALL")).GetProperty("error").GetString());
        var narrowed = await RefreshAsync(app, Refresh(first), scope: "READ");
        Assert.Equal("READ", narrowed.GetProperty("scope").GetString());
        var token = narrowed.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(_f, token).PostJsonAsync("/rest/api/space", new { key = "NW", name = "N" })).StatusCode);
        // Narrowed for good: a later refresh cannot widen again.
        Assert.Equal("READ", (await RefreshAsync(app, Refresh(narrowed))).GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Refresh_IsBoundToClient_Lifetime_AndUser()
    {
        var app = await ClientAsync(_admin);
        var other = await ClientAsync(_admin);
        var first = await TokensAsync(_f, app);
        Assert.Equal("invalid_grant", (await RefreshAsync(other, Refresh(first), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        Assert.Equal("invalid_grant", (await RefreshAsync(app, Refresh(first), HttpStatusCode.BadRequest, redirect: "https://elsewhere.example/cb")).GetProperty("error").GetString());
        Assert.Equal("invalid_grant", (await RefreshAsync(app, "kbr_" + new string('A', 43), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        // None of those used it up.
        var second = await RefreshAsync(app, Refresh(first));
        // Idle too long: expired.
        await _f.WithDbAsync(db => db.OAuthRefreshTokens.Where(r => r.RotatedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
        Assert.Equal("invalid_grant", (await RefreshAsync(app, Refresh(second), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        // A disabled user cannot refresh.
        var third = await TokensAsync(_f, app, user: "olga");
        await _f.WithDbAsync(db => db.Users.Where(u => u.UserName == "olga").ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivatedAt, DateTimeOffset.UtcNow), Ct));
        Assert.Equal("invalid_grant", (await RefreshAsync(app, Refresh(third), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
    }

    /// <summary>At most 25 live families per user and client: the 26th authorization revokes the least recently used.</summary>
    [Fact]
    public async Task The26thFamily_RevokesTheLeastRecentlyUsed()
    {
        var app = await ClientAsync(_admin);
        var families = new List<JsonElement>();
        for (var i = 0; i < 26; i++)
        {
            if (i == 25) families[0] = await RefreshAsync(app, Refresh(families[0])); // the first is used again before the 26th
            families.Add(await TokensAsync(_f, app));
        }
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(25, await db.OAuthTokenFamilies.CountAsync(f => f.RevokedAt == null, Ct));
            Assert.Equal("too many families", (await db.OAuthTokenFamilies.SingleAsync(f => f.RevokedAt != null, Ct)).RevokeReason);
        });
        Assert.False(await LiveAsync(families[1])); // the least recently used was the second
        Assert.True(await LiveAsync(families[25]));
    }
}
