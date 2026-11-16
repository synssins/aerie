using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web.DataCenter;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 5: the authorize endpoint and consent (identity.md, The authorization request, Consent).</summary>
public sealed class OAuthAuthorizeTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private const string Cb = "https://app.example/cb";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private static readonly string Challenge = OAuthSecrets.S256(Verifier);

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>A browser: it keeps cookies and does not follow redirects, so each Location can be read.</summary>
    private HttpClient Browser() => _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });

    private async Task<HttpClient> SignedInAsync(string name)
    {
        if (await UserIdAsync(_f, name) is null) await UserAsync(_f, name);
        var b = Browser();
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).StatusCode);
        return b;
    }

    private async Task<string> ClientAsync(string type = "confidential", bool? pkce = null, string[]? scopes = null, string[]? uris = null)
    {
        var r = await _admin.PostAsJsonAsync("/api/v1/admin/oauth/clients", new { name = "Desk app", type, redirectUris = uris ?? [Cb], scopes = scopes ?? ["READ", "WRITE"], requirePkce = pkce }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("client").GetProperty("clientId").GetString()!;
    }

    private static string Url(string clientId, string? redirect = Cb, string? scope = "READ", string? state = "xyz", string? challenge = "", string? method = "S256", string responseType = "code", string extra = "")
    {
        var q = new List<string> { "response_type=" + responseType, "client_id=" + Uri.EscapeDataString(clientId) };
        if (redirect is not null) q.Add("redirect_uri=" + Uri.EscapeDataString(redirect));
        if (scope is not null) q.Add("scope=" + Uri.EscapeDataString(scope));
        if (state is not null) q.Add("state=" + Uri.EscapeDataString(state));
        if (challenge is not null) q.Add("code_challenge=" + (challenge.Length == 0 ? Challenge : challenge));
        if (method is not null) q.Add("code_challenge_method=" + method);
        return "/rest/oauth2/latest/authorize?" + string.Join('&', q) + extra;
    }

    private static Dictionary<string, string> QueryOf(Uri location) =>
        QueryHelpers.ParseQuery(location.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);

    /// <summary>Starts a flow and follows it to the consent page's request id.</summary>
    private static async Task<Guid> PendingAsync(HttpClient browser, string url)
    {
        var r = await browser.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var location = r.Headers.Location!.ToString();
        Assert.StartsWith("/oauth/consent/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/oauth/consent/".Length..]);
    }

    private static async Task<Uri> DecideAsync(HttpClient browser, Guid id, bool allow)
    {
        var r = await browser.PostAsJsonAsync($"/api/v1/oauth/authorize-requests/{id}/decision", new { allow }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new Uri((await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("redirectTo").GetString()!);
    }

    // auth.oauth.authorize.bad_redirect and .unknown_client: an error page, never a redirect.
    [Fact]
    public async Task BadClientOrRedirect_IsAnErrorPage_NeverARedirect()
    {
        var id = await ClientAsync();
        var b = await SignedInAsync("ravi");
        foreach (var url in new[]
                 {
                     Url("nope"), Url(id, "https://evil.example/cb"), Url(id, Cb + "/"), Url(id, "https://APP.example/cb?x=1"), Url(id, redirect: null),
                     Url(id) + "&redirect_uri=" + Uri.EscapeDataString(Cb), // the redirect URI twice
                 })
        {
            var r = await b.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Null(r.Headers.Location);
            Assert.StartsWith("text/html", r.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        }
        // A disabled client too.
        var cid = (await _f.WithDbValueAsync(db => db.OAuthClients.SingleAsync(c => c.ClientId == id, Ct))).Id;
        await _admin.PutAsJsonAsync($"/api/v1/admin/oauth/clients/{cid}", new { enabled = false }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.GetAsync(Url(id), Ct)).StatusCode);
    }

    // missing_pkce, plain_pkce, bad_scope and the other errors go back to the client, with state.
    [Theory]
    [InlineData("missing_pkce", "invalid_request")]
    [InlineData("plain_pkce", "invalid_request")]
    [InlineData("no_method", "invalid_request")]
    [InlineData("short_challenge", "invalid_request")]
    [InlineData("bad_scope", "invalid_scope")]
    [InlineData("special_scope", "invalid_scope")]
    [InlineData("no_scope", "invalid_scope")]
    [InlineData("token_type", "unsupported_response_type")]
    [InlineData("repeated", "invalid_request")]
    public async Task Errors_GoBackToTheClient_WithState(string what, string error)
    {
        var id = await ClientAsync(scopes: ["READ"]);
        var url = what switch
        {
            "missing_pkce" => Url(id, challenge: null, method: null),
            "plain_pkce" => Url(id, challenge: Verifier, method: "plain"),
            "no_method" => Url(id, method: null),
            "short_challenge" => Url(id, challenge: "abc"),
            "bad_scope" => Url(id, scope: "READ WRITE"),
            "special_scope" => Url(id, scope: "READ_ALL"),
            "no_scope" => Url(id, scope: null),
            "token_type" => Url(id, responseType: "token"),
            _ => Url(id, extra: "&scope=READ"),
        };
        var r = await (await SignedInAsync("ravi")).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var location = r.Headers.Location!;
        Assert.Equal(Cb, location.GetLeftPart(UriPartial.Path));
        Assert.Empty(location.Fragment);
        var q = QueryOf(location);
        Assert.Equal((error, "xyz"), (q["error"], q["state"]));
        Assert.False(q.ContainsKey("code"));
    }

    // The flow: not signed in, sign in, consent, back with a code and the state byte for byte; a second time skips the page.
    [Fact]
    public async Task Consent_Allow_ThenRemembered()
    {
        var id = await ClientAsync();
        await UserAsync(_f, "ravi");
        var b = Browser();
        const string state = "a b+c/=é&x";
        var pending = await PendingAsync(b, Url(id, state: state, scope: "READ"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync($"/api/v1/oauth/authorize-requests/{pending}", Ct)).StatusCode); // sign in first
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ravi", password = Password }, Ct)).StatusCode);
        var read = await (await b.GetAsync($"/api/v1/oauth/authorize-requests/{pending}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Desk app", "READ", 0), (read.GetProperty("clientName").GetString(), read.GetProperty("scopes")[0].GetString(), read.GetProperty("alreadyGranted").GetArrayLength()));
        var back = await DecideAsync(b, pending, allow: true);
        var q = QueryOf(back);
        Assert.Equal(Cb, back.GetLeftPart(UriPartial.Path));
        Assert.Equal(state, q["state"]);
        Assert.Matches("^[A-Za-z0-9]{43}$", q["code"]);
        // Decided once: the request is gone for a second decision and for reading.
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/v1/oauth/authorize-requests/{pending}/decision", new { allow = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/oauth/authorize-requests/{pending}", Ct)).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            var code = await db.OAuthCodes.SingleAsync(Ct);
            Assert.Equal(OAuthSecrets.Hash(q["code"]), code.Hash);
            Assert.Equal((Cb, "READ", Challenge), (code.RedirectUri, code.Scopes, code.CodeChallenge));
            Assert.Equal(300, (int)(code.ExpiresAt - code.CreatedAt).TotalSeconds);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthConsentGrant && a.DataJson.Contains(id), Ct));
        });

        // auth.oauth.consent.remembered: the same or fewer scopes go straight back with a code.
        var again = await b.GetAsync(Url(id, scope: "READ", state: "two"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal(("two", true), (QueryOf(again.Headers.Location!)["state"], QueryOf(again.Headers.Location!).ContainsKey("code")));
        // More scopes ask again, for the new total.
        var more = await PendingAsync(b, Url(id, scope: "READ WRITE"));
        var asked = await (await b.GetAsync($"/api/v1/oauth/authorize-requests/{more}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["READ"], asked.GetProperty("alreadyGranted").EnumerateArray().Select(s => s.GetString()));
        await DecideAsync(b, more, allow: true);
        await _f.WithDbAsync(async db => Assert.Equal("READ WRITE", (await db.OAuthGrants.SingleAsync(Ct)).Scopes));
    }

    // auth.oauth.consent.deny
    [Fact]
    public async Task Consent_Deny_SendsAccessDenied_WithTheState()
    {
        var id = await ClientAsync();
        var b = await SignedInAsync("ravi");
        var back = await DecideAsync(b, await PendingAsync(b, Url(id, state: "keep-me")), allow: false);
        var q = QueryOf(back);
        Assert.Equal(("access_denied", "keep-me", false), (q["error"], q["state"], q.ContainsKey("code")));
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.OAuthGrants.AnyAsync(Ct));
            Assert.False(await db.OAuthCodes.AnyAsync(Ct));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.OAuthConsentDeny, Ct));
        });
    }

    /// <summary>A request started by one user cannot be read or decided by another; an expired one by nobody.</summary>
    [Fact]
    public async Task ARequest_BelongsToItsUser_AndExpires()
    {
        var id = await ClientAsync();
        var ravi = await SignedInAsync("ravi");
        var olga = await SignedInAsync("olga");
        var pending = await PendingAsync(ravi, Url(id));
        Assert.Equal(HttpStatusCode.NotFound, (await olga.GetAsync($"/api/v1/oauth/authorize-requests/{pending}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await olga.PostAsJsonAsync($"/api/v1/oauth/authorize-requests/{pending}/decision", new { allow = true }, Ct)).StatusCode);
        await _f.WithDbAsync(db => db.OAuthAuthorizeRequests.ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await ravi.PostAsJsonAsync($"/api/v1/oauth/authorize-requests/{pending}/decision", new { allow = true }, Ct)).StatusCode);
        // Opened before sign-in, the request is for whoever signs in on that browser; once decided it is that user's.
        var anon = Browser();
        var open = await PendingAsync(anon, Url(id));
        await _f.WithDbAsync(async db => Assert.Null((await db.OAuthAuthorizeRequests.SingleAsync(r => r.Id == open, Ct)).UserId));
    }

    /// <summary>PKCE: required for a public client and, by default, a confidential one; a confidential client may turn it off.</summary>
    [Fact]
    public async Task Pkce_AppliesAsTheClientSays()
    {
        var b = await SignedInAsync("ravi");
        var pub = await ClientAsync("public", uris: ["http://127.0.0.1/cb"]);
        Assert.Equal("invalid_request", QueryOf((await b.GetAsync(Url(pub, "http://127.0.0.1/cb", challenge: null, method: null), Ct)).Headers.Location!)["error"]);
        // A loopback redirect URI matches whatever port the app listens on.
        await PendingAsync(b, Url(pub, "http://127.0.0.1:53111/cb"));
        var old = await ClientAsync(pkce: false, uris: ["https://old.example/cb"]);
        await PendingAsync(b, Url(old, "https://old.example/cb", challenge: null, method: null));
        // sha256 is read as S256.
        await PendingAsync(b, Url(pub, "http://127.0.0.1/cb", method: "sha256"));
    }

    /// <summary>The redirect matching rules (identity.md, Redirect URI rules).</summary>
    [Theory]
    [InlineData("https://app.example/cb", "https://app.example/cb", true)]
    [InlineData("https://app.example/cb", "HTTPS://APP.EXAMPLE/cb", true)]
    [InlineData("https://app.example/cb", "https://app.example/CB", false)]
    [InlineData("https://app.example/cb", "https://app.example/cb/", false)]
    [InlineData("https://app.example/cb", "https://app.example/cb?x=1", false)]
    [InlineData("https://app.example/cb", "https://app.example:443/cb", false)]
    [InlineData("https://app.example:8443/cb", "https://app.example:9443/cb", false)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:61234/cb", true)]
    [InlineData("http://localhost:3000/cb", "http://localhost:4000/cb", true)]
    [InlineData("http://[::1]/cb", "http://[::1]:5000/cb", true)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:61234/other", false)]
    [InlineData("com.example.app:/callback", "com.example.app:/callback", true)]
    [InlineData("com.example.app:/callback", "com.example.app:/callback2", false)]
    [InlineData("https://app.example/cb", "https://user@app.example/cb", false)]
    public void Redirects_MatchExactly(string registered, string presented, bool matches) => Assert.Equal(matches, OAuthRedirects.Matches(registered, presented));
}
