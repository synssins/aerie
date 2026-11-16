using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;
using static Kb.Tests.Integration.DataCenter.OAuthKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 9: revocation, connected applications and the server metadata (identity.md, Revocation, Consent, Server metadata).</summary>
public sealed class OAuthRevokeTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<HttpResponseMessage> RevokeAsync(App app, string? token, string? hint = null, string? secret = null)
    {
        var form = new Dictionary<string, string> { ["client_id"] = app.ClientId };
        if (token is not null) form["token"] = token;
        if (hint is not null) form["token_type_hint"] = hint;
        if ((secret ?? app.Secret) is { } s) form["client_secret"] = s;
        var r = await _f.NewClient().PostAsync("/rest/oauth2/latest/revoke", new FormUrlEncodedContent(form), Ct);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        return r;
    }

    private async Task<bool> LiveAsync(JsonElement tokens) =>
        await _f.WithDbValueAsync(db => OAuthStore.LiveAccessTokenAsync(db, tokens.GetProperty("access_token").GetString()!, DateTimeOffset.UtcNow, Ct)) is not null;

    private static string Refresh(JsonElement t) => t.GetProperty("refresh_token").GetString()!;
    private static string Access(JsonElement t) => t.GetProperty("access_token").GetString()!;

    private Task<JsonElement> RefreshAsync(App app, string refresh, HttpStatusCode expected = HttpStatusCode.OK) =>
        PostTokenAsync(_f, app, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, expected);

    // auth.oauth.revoke.refresh: after revoking, both refresh and access tokens fail.
    [Fact]
    public async Task RevokingARefreshToken_EndsTheFamily()
    {
        var app = await ClientAsync(_admin);
        var t = await TokensAsync(_f, app);
        var r = await RevokeAsync(app, Refresh(t), "refresh_token");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsStringAsync(Ct));
        Assert.False(await LiveAsync(t));
        Assert.Equal("invalid_grant", (await RefreshAsync(app, Refresh(t), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        await (await Bearer(_f, Access(t)).GetAsync("/rest/api/user/current", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthGrantRevoke && a.DataJson.Contains("refresh"), Ct)));
    }

    [Fact]
    public async Task RevokingAnAccessToken_EndsOnlyIt()
    {
        var app = await ClientAsync(_admin, "public", ["READ"]);
        var t = await TokensAsync(_f, app);
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(app, Access(t), "refresh_token")).StatusCode); // a wrong hint still finds it
        Assert.False(await LiveAsync(t));
        Assert.True(await LiveAsync(await RefreshAsync(app, Refresh(t))));
    }

    // auth.oauth.revoke.unknown: 200, whatever the token; another client's is left alone.
    [Fact]
    public async Task Revoke_Answers200_AndCannotProbe()
    {
        var app = await ClientAsync(_admin);
        var other = await ClientAsync(_admin);
        var t = await TokensAsync(_f, app);
        foreach (var token in new[] { "nonsense", "kbo_" + new string('A', 43), "kbr_" + new string('B', 43) })
            Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(app, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(other, Refresh(t))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(other, Access(t))).StatusCode);
        Assert.True(await LiveAsync(t)); // another client's token is ignored
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(app, Refresh(t))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(app, Refresh(t))).StatusCode); // already revoked
        // Bad client authentication and a missing token are errors.
        var bad = await RevokeAsync(app, Refresh(t), secret: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("invalid_client", (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        var missing = await RevokeAsync(app, null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_request", (await missing.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    // auth.oauth.user_revokes_grant: the user disconnects the application in their profile; its tokens end at once.
    [Fact]
    public async Task AUser_DisconnectsAnApplication()
    {
        var app = await ClientAsync(_admin);
        var t = await TokensAsync(_f, app, "READ WRITE");
        await TokensAsync(_f, app, user: "olga");
        var ravi = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await ravi.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ravi", password = Password }, Ct)).StatusCode);
        var grants = await ravi.GetFromJsonAsync<JsonElement>("/api/v1/me/oauth/grants", Ct);
        Assert.Equal(1, grants.GetArrayLength()); // only ravi's own
        var g = grants[0];
        Assert.Equal(("Desk app", "READ,WRITE"), (g.GetProperty("clientName").GetString(), string.Join(',', g.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()))));
        var olgas = await _f.WithDbValueAsync(db => db.OAuthGrants.Where(x => x.Id != g.GetProperty("id").GetGuid()).Select(x => x.Id).SingleAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await ravi.DeleteAsync($"/api/v1/me/oauth/grants/{olgas}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/api/v1/me/oauth/grants/{olgas}", Ct)).StatusCode); // an Admin too
        Assert.Equal(HttpStatusCode.NoContent, (await ravi.DeleteAsync($"/api/v1/me/oauth/grants/{g.GetProperty("id").GetGuid()}", Ct)).StatusCode);
        Assert.False(await LiveAsync(t));
        Assert.Equal(0, (await ravi.GetFromJsonAsync<JsonElement>("/api/v1/me/oauth/grants", Ct)).GetArrayLength());
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.OAuthGrants.CountAsync(Ct)); // olga's stays
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthGrantRevoke && a.DataJson.Contains("user"), Ct));
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("/api/v1/me/oauth/grants", Ct)).StatusCode);
    }

    // auth.oauth.metadata
    [Fact]
    public async Task Metadata_ListsTheEndpointsAndS256()
    {
        var m = await _f.NewClient().GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server", Ct);
        var issuer = m.GetProperty("issuer").GetString()!;
        Assert.StartsWith("http", issuer, StringComparison.Ordinal);
        Assert.Equal(issuer + "/rest/oauth2/latest/authorize", m.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(issuer + "/rest/oauth2/latest/token", m.GetProperty("token_endpoint").GetString());
        Assert.Equal(issuer + "/rest/oauth2/latest/revoke", m.GetProperty("revocation_endpoint").GetString());
        string[] List(string name) => [.. m.GetProperty(name).EnumerateArray().Select(x => x.GetString()!)];
        Assert.Equal(["READ", "WRITE", "ADMIN", "SYSTEM_ADMIN"], List("scopes_supported"));
        Assert.Equal(["code"], List("response_types_supported"));
        Assert.Equal(["authorization_code", "refresh_token"], List("grant_types_supported"));
        Assert.Equal(["S256"], List("code_challenge_methods_supported"));
        Assert.Equal(["client_secret_post", "client_secret_basic", "none"], List("token_endpoint_auth_methods_supported"));
        Assert.Equal(["client_secret_post", "client_secret_basic", "none"], List("revocation_endpoint_auth_methods_supported"));
    }
}
