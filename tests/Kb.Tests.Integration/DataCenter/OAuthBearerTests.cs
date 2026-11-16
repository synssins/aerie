using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;
using static Kb.Tests.Integration.DataCenter.OAuthKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 7: OAuth access tokens on /rest/api (identity.md, Errors on protected calls, Scopes, Sessions and aerie's own API).</summary>
public sealed class OAuthBearerTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _admin.DefaultRequestHeaders.Add("X-Atlassian-Token", "no-check");
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<string> AccessAsync(string scope = "READ", string user = "ravi", string[]? clientScopes = null) =>
        (await TokensAsync(_f, await ClientAsync(_admin, scopes: clientScopes), scope, user)).GetProperty("access_token").GetString()!;

    private async Task<Guid> SpaceAsync(string key)
    {
        var id = long.Parse((await (await _admin.PostJsonAsync("/rest/api/space", new { key, name = key })).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetRawText(), System.Globalization.CultureInfo.InvariantCulture);
        return await _f.WithDbValueAsync(db => db.DcSpaces.Where(s => s.Id == id).Select(s => s.SpaceId).SingleAsync(Ct));
    }

    // auth.oauth.pkce.full_flow, the call: the token acts as its user.
    [Fact]
    public async Task AToken_ActsAsItsUser_AndSetsNoCookie()
    {
        var r = await Bearer(_f, await AccessAsync()).GetAsync("/rest/api/user/current", Ct);
        Assert.Equal("ravi", (await r.JsonAsync(HttpStatusCode.OK)).GetProperty("username").GetString());
        Assert.False(r.Headers.Contains("Set-Cookie"));
    }

    // auth.oauth.read_scope.write_refused
    [Fact]
    public async Task ReadScope_CannotWrite()
    {
        await SpaceAsync("OPS");
        var r = await Bearer(_f, await AccessAsync()).PostJsonAsync("/rest/api/content", new { type = "page", title = "No", space = new { key = "OPS" } });
        await r.ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Bearer error=\"insufficient_scope\", scope=\"WRITE\"", r.Headers.WwwAuthenticate.ToString());
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.TokenUseDenied && a.DataJson.Contains("scope") && a.DataJson.Contains("kbo_"), Ct)));
    }

    // auth.oauth.write_scope.ok: a WRITE token creates a page where its user is a Contributor; the role still decides elsewhere.
    [Fact]
    public async Task WriteScope_DoesWhatTheUserMay()
    {
        var space = await SpaceAsync("WRK");
        await SpaceAsync("NOPE");
        var ravi = await UserAsync(_f, "ravi");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = ravi.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var t = Bearer(_f, await AccessAsync("WRITE"));
        Assert.Equal("Made", (await (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "Made", space = new { key = "WRK" } })).JsonAsync(HttpStatusCode.OK)).GetProperty("title").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await t.PostJsonAsync("/rest/api/content", new { type = "page", title = "Elsewhere", space = new { key = "NOPE" } })).StatusCode);
    }

    // auth.oauth.admin_scope.non_admin_user: ADMIN on a token of someone who is not an administrator gains nothing beyond WRITE.
    [Fact]
    public async Task AdminScope_OfANonAdmin_GainsNothing()
    {
        await SpaceAsync("ADM");
        await UserAsync(_f, "ravi");
        var token = await AccessAsync("ADMIN", clientScopes: ["READ", "WRITE", "ADMIN"]);
        var viaToken = await Bearer(_f, token).DeleteAsync("/rest/api/space/ADM", Ct);
        var viaPassword = await Basic(_f, "ravi").DeleteAsync("/rest/api/space/ADM", Ct);
        Assert.Equal(viaPassword.StatusCode, viaToken.StatusCode);
        Assert.NotEqual(HttpStatusCode.Accepted, viaToken.StatusCode);
    }

    // auth.oauth.token_not_on_aerie_api
    [Fact]
    public async Task AToken_IsRefusedOnAeriesOwnApi()
    {
        var token = await AccessAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(_f, token).GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(_f, token).GetAsync("/api/v1/spaces", Ct)).StatusCode);
    }

    // auth.oauth.token_in_basic: the owner's user name and the token in the password field.
    [Fact]
    public async Task AToken_WorksInTheBasicPasswordField_WithTheOwnersName()
    {
        var token = await AccessAsync();
        await UserAsync(_f, "olga");
        Assert.Equal("ravi", (await Basic(_f, "RAVI", token).GetJsonAsync("/rest/api/user/current")).GetProperty("username").GetString());
        await (await Basic(_f, "olga", token).GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
    }

    /// <summary>Expired, revoked, a disabled client and a disabled user each give 401 invalid_token at once; a token in the query string is ignored.</summary>
    [Fact]
    public async Task RefusedTokens_Are401InvalidToken()
    {
        var app = await ClientAsync(_admin);
        async Task<string> Fresh() => (await TokensAsync(_f, app)).GetProperty("access_token").GetString()!;
        async Task Refused(string token)
        {
            var r = await Bearer(_f, token).GetAsync("/rest/api/user/current", Ct);
            await r.ErrorAsync(HttpStatusCode.Unauthorized);
            Assert.Equal("Bearer error=\"invalid_token\"", r.Headers.WwwAuthenticate.ToString());
        }
        var expired = await Fresh();
        await _f.WithDbAsync(db => db.OAuthAccessTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
        await Refused(expired);
        var revoked = await Fresh();
        await _f.WithDbAsync(db => db.OAuthTokenFamilies.ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), Ct));
        await Refused(revoked);
        var off = await Fresh();
        (await _admin.PutAsJsonAsync($"/api/v1/admin/oauth/clients/{app.Id}", new { enabled = false }, Ct)).EnsureSuccessStatusCode();
        await Refused(off);
        (await _admin.PutAsJsonAsync($"/api/v1/admin/oauth/clients/{app.Id}", new { enabled = true }, Ct)).EnsureSuccessStatusCode();
        var gone = await Fresh();
        Assert.Equal(HttpStatusCode.OK, (await Bearer(_f, gone).GetAsync("/rest/api/user/current", Ct)).StatusCode);
        await _f.WithDbAsync(db => db.Users.Where(u => u.UserName == "ravi").ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivatedAt, DateTimeOffset.UtcNow), Ct));
        await Refused(gone);
        await Refused(Kb.Core.Identity.OAuthSecrets.NewAccessToken()); // unknown
        await _f.WithDbAsync(async db =>
        {
            var reasons = await db.AuditEntries.Where(a => a.Action == AuditActions.TokenUseDenied).Select(a => a.DataJson).ToListAsync(Ct);
            foreach (var why in new[] { "expired", "revoked", "client disabled", "user disabled" }) Assert.Contains(reasons, d => d.Contains(why, StringComparison.Ordinal));
            var log = string.Join('|', await db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct));
            foreach (var t in new[] { expired, revoked, off, gone }) Assert.DoesNotContain(t, log, StringComparison.Ordinal);
        });

        // Only in the Authorization header: in the query string a token is not a credential.
        await _f.WithDbAsync(db => db.Users.Where(u => u.UserName == "ravi").ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivatedAt, (DateTimeOffset?)null), Ct));
        var live = await Fresh();
        var anonymous = await _f.NewClient().GetAsync("/rest/api/user/current?access_token=" + live, Ct);
        Assert.DoesNotContain("\"ravi\"", await anonymous.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
