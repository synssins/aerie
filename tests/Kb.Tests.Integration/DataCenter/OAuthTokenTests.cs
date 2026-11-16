using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 6: the token endpoint's code exchange (identity.md, The token endpoint, Authorization codes).</summary>
public sealed class OAuthTokenTests : IAsyncLifetime
{
    private readonly KbAppFactory _f;
    private HttpClient _admin = null!;
    private const string Cb = "https://app.example/cb";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string TokenPath = "/rest/oauth2/latest/token";

    public OAuthTokenTests() : this(null) { }

    private OAuthTokenTests(IDictionary<string, string>? settings) => _f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: settings);

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private sealed record App(string ClientId, string? Secret);

    private async Task<App> ClientAsync(string type = "confidential", string[]? scopes = null)
    {
        var r = await _admin.PostAsJsonAsync("/api/v1/admin/oauth/clients", new { name = "Desk app", type, redirectUris = new[] { Cb }, scopes = scopes ?? ["READ", "WRITE"] }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new App(j.GetProperty("client").GetProperty("clientId").GetString()!, j.GetProperty("clientSecret").GetString());
    }

    /// <summary>A user signs in, consents, and the browser comes back with a code.</summary>
    private async Task<string> CodeAsync(App app, string scope = "READ", string user = "ravi")
    {
        if (await UserIdAsync(_f, user) is null) await UserAsync(_f, user);
        var b = _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = user, password = Password }, Ct)).StatusCode);
        var r = await b.GetAsync($"/rest/oauth2/latest/authorize?response_type=code&client_id={app.ClientId}&redirect_uri={Uri.EscapeDataString(Cb)}&scope={Uri.EscapeDataString(scope)}&state=s&code_challenge={OAuthSecrets.S256(Verifier)}&code_challenge_method=S256", Ct);
        var location = r.Headers.Location!.ToString();
        if (location.StartsWith("/oauth/consent/", StringComparison.Ordinal))
        {
            var decided = await b.PostAsJsonAsync($"/api/v1/oauth/authorize-requests/{location["/oauth/consent/".Length..]}/decision", new { allow = true }, Ct);
            location = (await decided.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("redirectTo").GetString()!;
        }
        return QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
    }

    private static Dictionary<string, string> Exchange(App app, string code, string? verifier = Verifier, string redirect = Cb, bool secret = true)
    {
        var p = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = app.ClientId };
        if (verifier is not null) p["code_verifier"] = verifier;
        if (secret && app.Secret is not null) p["client_secret"] = app.Secret;
        return p;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(Dictionary<string, string> form, AuthenticationHeaderValue? auth = null, string query = "")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, TokenPath + query) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = auth;
        var r = await _f.NewClient().SendAsync(req, Ct);
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", r.Headers.Pragma.ToString(), StringComparison.Ordinal);
        return (r, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static void IsError(HttpResponseMessage r, JsonElement body, HttpStatusCode status, string error)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal(error, body.GetProperty("error").GetString());
    }

    private async Task<bool> LiveAsync(string access) => await _f.WithDbValueAsync(db => OAuthStore.LiveAccessTokenAsync(db, access, DateTimeOffset.UtcNow, Ct)) is not null;

    // auth.oauth.pkce.full_flow: a public client with PKCE gets tokens.
    [Fact]
    public async Task PublicClient_WithPkce_GetsTokens()
    {
        var app = await ClientAsync("public", ["READ"]);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (r, j) = await PostAsync(Exchange(app, await CodeAsync(app)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Matches("^kbo_[A-Za-z0-9]{43}$", j.GetProperty("access_token").GetString()!);
        Assert.Matches("^kbr_[A-Za-z0-9]{43}$", j.GetProperty("refresh_token").GetString()!);
        Assert.Equal(("bearer", 3600, "READ"), (j.GetProperty("token_type").GetString(), j.GetProperty("expires_in").GetInt32(), j.GetProperty("scope").GetString()));
        Assert.InRange(j.GetProperty("created_at").GetInt64(), before, before + 60);
        Assert.True(await LiveAsync(j.GetProperty("access_token").GetString()!));
        await _f.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthTokenIssue && a.DataJson.Contains(app.ClientId), Ct));
            var log = string.Join('|', await db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct));
            Assert.DoesNotContain(j.GetProperty("access_token").GetString()!, log, StringComparison.Ordinal);
            Assert.DoesNotContain(j.GetProperty("refresh_token").GetString()!, log, StringComparison.Ordinal);
        });
    }

    // auth.oauth.confidential.full_flow: the secret in the query string of the POST, then with HTTP Basic.
    [Fact]
    public async Task ConfidentialClient_SecretInTheQuery_OrBasic()
    {
        var app = await ClientAsync();
        var p = Exchange(app, await CodeAsync(app));
        var query = "?" + string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var (r, j) = await PostAsync([], query: query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(await LiveAsync(j.GetProperty("access_token").GetString()!));

        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:{app.Secret}")));
        var viaBasic = Exchange(app, await CodeAsync(app, user: "olga"), secret: false);
        viaBasic.Remove("client_id");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(viaBasic, basic)).Response.StatusCode);
    }

    // auth.oauth.code.reuse: the second exchange is invalid_grant and the first exchange's tokens stop working.
    [Fact]
    public async Task ACode_IsSingleUse_AndReuseRevokesItsTokens()
    {
        var app = await ClientAsync();
        var form = Exchange(app, await CodeAsync(app));
        var (_, first) = await PostAsync(form);
        var access = first.GetProperty("access_token").GetString()!;
        Assert.True(await LiveAsync(access));
        var (r, j) = await PostAsync(form);
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
        Assert.False(await LiveAsync(access));
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal("code reused", (await db.OAuthTokenFamilies.SingleAsync(Ct)).RevokeReason);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthTokenReuse, Ct));
            Assert.True(await db.OAuthGrants.AnyAsync(Ct)); // the consent stays
        });
    }

    /// <summary>Two exchanges of one code at once: one wins, the other is reuse, and the winner's tokens are revoked too.</summary>
    [Fact]
    public async Task ACode_ExchangedTwiceAtOnce_GivesOneFamily_ThenRevokesIt()
    {
        var app = await ClientAsync();
        var form = Exchange(app, await CodeAsync(app));
        var both = await Task.WhenAll(PostAsync(form), PostAsync(form));
        Assert.Single(both, x => x.Response.StatusCode == HttpStatusCode.OK);
        Assert.Single(both, x => x.Response.StatusCode == HttpStatusCode.BadRequest);
        Assert.False(await LiveAsync(both.Single(x => x.Response.StatusCode == HttpStatusCode.OK).Body.GetProperty("access_token").GetString()!));
        await _f.WithDbAsync(async db => Assert.Equal(1, await db.OAuthTokenFamilies.CountAsync(Ct)));
    }

    // auth.oauth.code.wrong_verifier, wrong_redirect, and the rest of the code checks.
    [Fact]
    public async Task TheCode_IsBoundToVerifierRedirectAndClient()
    {
        var app = await ClientAsync();
        var other = await ClientAsync();
        var (r, j) = await PostAsync(Exchange(app, await CodeAsync(app), verifier: "x" + Verifier[1..]));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
        (r, j) = await PostAsync(Exchange(app, await CodeAsync(app), verifier: null));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_request");
        (r, j) = await PostAsync(Exchange(app, await CodeAsync(app), verifier: "short"));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_request");
        (r, j) = await PostAsync(Exchange(app, await CodeAsync(app), redirect: Cb + "/"));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
        (r, j) = await PostAsync(Exchange(other, await CodeAsync(app))); // another client's code
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
        (r, j) = await PostAsync(Exchange(app, "not-a-code"));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
        var late = await CodeAsync(app);
        await _f.WithDbAsync(db => db.OAuthCodes.Where(c => c.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
        (r, j) = await PostAsync(Exchange(app, late));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
        // A disabled user's code is refused.
        var olgas = await CodeAsync(app, user: "olga");
        await _f.WithDbAsync(db => db.Users.Where(u => u.UserName == "olga").ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivatedAt, DateTimeOffset.UtcNow), Ct));
        (r, j) = await PostAsync(Exchange(app, olgas));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_grant");
    }

    // auth.oauth.client.bad_secret: 401 invalid_client, with a Basic challenge when Basic was used.
    [Fact]
    public async Task BadClientCredentials_AreInvalidClient()
    {
        var app = await ClientAsync();
        var code = await CodeAsync(app);
        var p = Exchange(app, code);
        p["client_secret"] = "wrong";
        var (r, j) = await PostAsync(p);
        IsError(r, j, HttpStatusCode.Unauthorized, "invalid_client");
        Assert.Empty(r.Headers.WwwAuthenticate);
        var basic = Exchange(app, code, secret: false);
        basic.Remove("client_id");
        (r, j) = await PostAsync(basic, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:wrong"))));
        IsError(r, j, HttpStatusCode.Unauthorized, "invalid_client");
        Assert.Equal("Basic", r.Headers.WwwAuthenticate.Single().Scheme);
        var none = Exchange(app, code, secret: false);
        (r, j) = await PostAsync(none);
        IsError(r, j, HttpStatusCode.Unauthorized, "invalid_client");
        none["client_id"] = "nobody";
        (r, j) = await PostAsync(none);
        IsError(r, j, HttpStatusCode.Unauthorized, "invalid_client");
        // Parameters and Basic naming the client differently.
        (r, j) = await PostAsync(Exchange(app, code), new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:other"))));
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_request");
        // None of these used the code: it still works.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Exchange(app, code))).Response.StatusCode);
    }

    [Fact]
    public async Task GrantTypes_AndParameters()
    {
        var app = await ClientAsync();
        var code = await CodeAsync(app);
        var p = Exchange(app, code);
        p["grant_type"] = "password";
        var (r, j) = await PostAsync(p);
        IsError(r, j, HttpStatusCode.BadRequest, "unsupported_grant_type");
        p["grant_type"] = "client_credentials";
        (r, j) = await PostAsync(p);
        IsError(r, j, HttpStatusCode.BadRequest, "unsupported_grant_type");
        p.Remove("grant_type");
        (r, j) = await PostAsync(p);
        IsError(r, j, HttpStatusCode.BadRequest, "invalid_request");
        // The body wins over the query string.
        p["grant_type"] = "authorization_code";
        var (ok, _) = await PostAsync(p, query: "?grant_type=password&code=nonsense");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // auth.ratelimit.token: past the limit, 429 temporarily_unavailable with Retry-After, before any credential is checked.
    [Fact]
    public async Task TheTokenEndpoint_IsLimited()
    {
        await using var t = new OAuthTokenTests(new Dictionary<string, string> { ["Kb:Auth:OAuthTokenRequestsPerMinute"] = "3" });
        await t.InitializeAsync();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.BadRequest, (await t.PostAsync(new Dictionary<string, string> { ["client_id"] = "x" + i })).Response.StatusCode);
        var (r, j) = await t.PostAsync(new Dictionary<string, string> { ["grant_type"] = "authorization_code" });
        IsError(r, j, HttpStatusCode.TooManyRequests, "temporarily_unavailable");
        Assert.NotNull(r.Headers.RetryAfter);
    }
}
