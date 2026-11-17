using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kb.Tests.Integration.Plugins;

/// <summary>
/// Sign-in methods (contract 0.8.0), with the sample plugin's "Sign in with Example ID" against a stand-in OpenID Connect provider:
/// the whole flow, each refusal, account linking, and what the listings show.
/// </summary>
public sealed class SignInMethodTests : IAsyncLifetime
{
    private const string Method = "kb.sample/example";
    private const string Start = "/api/v1/auth/methods/kb.sample/example/start";
    private const string Callback = "/api/v1/auth/methods/kb.sample/example/callback";
    private readonly StandInProvider _provider = new();
    private KbAppFactory _f;
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SignInMethodTests() =>
        _f = new(KbAppFactory.NewDbPath(), services: s => s.AddHttpClient(PluginHttp.ClientName("kb.sample")).ConfigurePrimaryHttpMessageHandler(() => _provider));

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Sample));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(Dictionary<string, string> values) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.sample/settings", new { values }, Ct)).EnsureSuccessStatusCode();

    private Task SetUpAsync(bool newAccounts = true) => SetAsync(new()
    {
        ["signIn.example.clientId"] = StandInProvider.ClientId, ["signIn.example.clientSecret"] = StandInProvider.Secret, ["signIn.example.newAccounts"] = newAccounts ? "true" : "false",
    });

    private HttpClient Browser() =>
        _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });

    /// <summary>
    /// Clicks the button, signs in at the provider (which issues the ID token <paramref name="token"/> makes from the request's nonce),
    /// and comes back. <paramref name="back"/> may change the callback's query. Answers where the app sent the browser at the end.
    /// </summary>
    private async Task<string> SignInAsync(HttpClient browser, Action<Dictionary<string, object>>? claims = null, string? returnTo = null,
        Func<Dictionary<string, string>, Dictionary<string, string>>? back = null, Func<Dictionary<string, object>, string>? token = null)
    {
        var start = await browser.GetAsync(Start + (returnTo is null ? "" : "?returnTo=" + Uri.EscapeDataString(returnTo)), Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var at = start.Headers.Location!;
        Assert.StartsWith("https://id.example.com/authorize?", at.ToString(), StringComparison.Ordinal);
        var ask = QueryHelpers.ParseQuery(at.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var code = _provider.Authorize(ask, claims, token);
        var query = new Dictionary<string, string> { ["code"] = code, ["state"] = ask["state"] };
        if (back is not null) query = back(query);
        var end = await browser.GetAsync(Back(query), Ct);
        Assert.Equal(HttpStatusCode.Redirect, end.StatusCode);
        return end.Headers.Location!.ToString();
    }

    private static string Back(Dictionary<string, string> query) => Callback + "?" + string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));

    private static async Task<string?> MessageAsync(HttpClient browser) =>
        (await browser.GetFromJsonAsync<JsonElement>("/api/v1/auth/methods/outcome", Ct)).GetProperty("message").GetString();

    private static async Task<JsonElement?> MeAsync(HttpClient browser)
    {
        var r = await browser.GetAsync("/api/v1/me", Ct);
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<JsonElement>(Ct) : null;
    }

    private async Task<List<(string Action, string Data)>> AuditAsync()
    {
        var rows = new List<(string, string)>();
        await _f.WithDbAsync(async db => rows = [.. (await db.AuditEntries.Where(a => a.Action.StartsWith("auth.") || a.Action.StartsWith("user.signin")).OrderBy(a => a.Sequence).ToListAsync(Ct))
            .Select(a => (a.Action, a.DataJson))]);
        return rows;
    }

    private async Task<KbUser> LocalUserAsync(string name, string email, bool admin = false)
    {
        await using var scope = _f.Services.CreateAsyncScope();
        var user = new KbUser { UserName = name, DisplayName = name, Email = email, IsAdmin = admin, CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, "user-password-0123456789")).Succeeded);
        return user;
    }

    [Fact]
    public async Task TheMethod_IsListed_WithItsRedirectAddressAndSettings_AndOnTheSignInScreen_OnlyOnceItHasAClientId()
    {
        var plugin = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/plugins", Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample");
        var method = Assert.Single(plugin.GetProperty("signInMethods").EnumerateArray());
        Assert.Equal(Method, method.GetProperty("id").GetString());
        Assert.Equal("Sign in with Example ID", method.GetProperty("name").GetString());
        Assert.Equal("https://localhost" + Callback, method.GetProperty("redirectAddress").GetString());
        var keys = plugin.GetProperty("settings").EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();
        Assert.Contains("signIn.example.clientId", keys);
        Assert.True(plugin.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "signIn.example.clientSecret").GetProperty("secret").GetBoolean());
        Assert.Equal("false", plugin.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "signIn.example.newAccounts").GetProperty("default").GetString());

        var anonymous = _f.NewClient();
        Assert.Empty(await anonymous.GetFromJsonAsync<JsonElement[]>("/api/v1/auth/methods", Ct) ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await Browser().GetAsync(Start, Ct)).StatusCode);

        await SetUpAsync();
        var listed = Assert.Single(await anonymous.GetFromJsonAsync<JsonElement[]>("/api/v1/auth/methods", Ct) ?? []);
        Assert.Equal(["id", "name"], listed.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Method, listed.GetProperty("id").GetString());

        // The secret is write-only: the settings say it is set, never what it is.
        var settings = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/plugins/kb.sample/settings", Ct);
        Assert.Contains("signIn.example.clientSecret", settings.GetProperty("secretsSet").EnumerateArray().Select(s => s.GetString()));
        Assert.DoesNotContain(StandInProvider.Secret, settings.GetRawText(), StringComparison.Ordinal);
        await _f.WithDbAsync(async db => Assert.DoesNotContain(StandInProvider.Secret,
            string.Join("", await db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct)) + string.Join("", await db.PluginSettingValues.Select(v => v.Value).ToListAsync(Ct)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFirstSignIn_MakesAnAccount_WhenNewAccountsAreOn_NeverAnAdmin_AndReturnsToTheLocalPath()
    {
        await SetUpAsync();
        var browser = Browser();
        Assert.Equal("/spaces/x?y=1", await SignInAsync(browser, returnTo: "/spaces/x?y=1"));
        var me = (await MeAsync(browser))!.Value;
        Assert.Equal("ann@example.org", me.GetProperty("userName").GetString());
        Assert.Equal("Ann Example", me.GetProperty("displayName").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        await _f.WithDbAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.UserName == "ann@example.org", Ct);
            Assert.Equal((UserSource.SignInMethod, StandInProvider.Issuer, "person-1", false), (u.Source, u.ExternalIssuer, u.ExternalId, u.IsAdmin));
            Assert.Null(u.PasswordHash);
            Assert.False(await db.SpaceRoleAssignments.AnyAsync(a => a.PrincipalId == u.Id.ToString(), Ct));
        });

        // The PKCE verifier went to the token endpoint and fits the challenge; the client secret went in Basic, never in the query.
        Assert.True(_provider.VerifierFitted);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{StandInProvider.ClientId}:{StandInProvider.Secret}")), _provider.LastAuthorization);

        // The break-glass sign-ins of the test are there too; this method's entries are these.
        var audit = (await AuditAsync()).Where(a => a.Data.Contains(Method, StringComparison.Ordinal)).ToList();
        Assert.Equal([AuditActions.SignInMethodStart, AuditActions.UserSignInCreate, AuditActions.SignIn], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.DoesNotContain(_provider.LastCode!, a.Data, StringComparison.Ordinal));

        // The second time, the link is used.
        var again = Browser();
        await SignInAsync(again);
        Assert.Equal("ann@example.org", (await MeAsync(again))!.Value.GetProperty("userName").GetString());
        await _f.WithDbAsync(async db => Assert.Equal(1, await db.Users.CountAsync(x => x.ExternalId == "person-1", Ct)));
    }

    [Fact]
    public async Task AnExistingAccount_IsLinkedByItsVerifiedEmail_AndAfterThatByIssuerAndSubjectAlone()
    {
        await SetUpAsync(newAccounts: false);
        var local = await LocalUserAsync("ann", "Ann@Example.org");
        var browser = Browser();
        await SignInAsync(browser);
        Assert.Equal("ann", (await MeAsync(browser))!.Value.GetProperty("userName").GetString());
        await _f.WithDbAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Id == local.Id, Ct);
            Assert.Equal((UserSource.Local, StandInProvider.Issuer, "person-1"), (u.Source, u.ExternalIssuer, u.ExternalId));
        });
        Assert.Contains(AuditActions.UserSignInLink, (await AuditAsync()).Select(a => a.Action));

        // The provider now gives another email, not verified: the link still finds the account.
        var later = Browser();
        await SignInAsync(later, c => (c["email"], c["email_verified"]) = ("other@example.net", false));
        Assert.Equal("ann", (await MeAsync(later))!.Value.GetProperty("userName").GetString());

        // The local password still works.
        Assert.True((await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ann", password = "user-password-0123456789" }, Ct)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task AnAdminAccount_Linked_StillNeedsItsOneTimeCode()
    {
        await SetUpAsync(newAccounts: false);
        await LocalUserAsync("boss", "ann@example.org", admin: true);
        var browser = Browser();
        await SignInAsync(browser);
        Assert.Null(await MeAsync(browser));
        Assert.Equal("enroll", (await browser.GetFromJsonAsync<JsonElement>("/api/v1/auth/methods/outcome", Ct)).GetProperty("step").GetString());
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "unverified", "email not verified" },
        { "no-account", "no account" },
        { "wrong-state", "wrong state" },
        { "wrong-nonce", "nonce is wrong" },
        { "bad-signature", "signature is wrong" },
        { "wrong-audience", "another audience" },
        { "expired", "has expired" },
        { "wrong-issuer", "another issuer" },
        { "unsigned", "signature is wrong" },
        { "provider-error", "provider answered access_denied" },
        { "plugin-check", "refused by the plugin" },
        { "deactivated", "deactivated" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedSignIn_OpensNoSession_IsAudited_AndLeavesAMessageForTheSignInScreen(string kind, string reason)
    {
        await SetUpAsync(newAccounts: kind != "no-account");
        if (kind == "plugin-check") await SetAsync(new() { ["SignInDomain"] = "corp.example" });
        if (kind == "deactivated")
        {
            var u = await LocalUserAsync("ann", "ann@example.org");
            await _f.WithDbAsync(async db => { (await db.Users.SingleAsync(x => x.Id == u.Id, Ct)).DeactivatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(Ct); });
        }
        var browser = Browser();
        var location = await SignInAsync(browser,
            claims: kind switch
            {
                "unverified" => c => c["email_verified"] = false,
                "wrong-nonce" => c => c["nonce"] = "another",
                "wrong-audience" => c => c["aud"] = "another-client",
                "expired" => c => (c["iat"], c["exp"]) = (DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()),
                "wrong-issuer" => c => c["iss"] = "https://other.example.com",
                _ => null,
            },
            back: kind switch
            {
                "wrong-state" => q => new(q) { ["state"] = "guessed" },
                "provider-error" => q => new() { ["error"] = "access_denied", ["state"] = q["state"] },
                _ => null,
            },
            token: kind switch
            {
                "bad-signature" => c => StandInProvider.Sign(c, new RsaSecurityKey(RSA.Create(2048)) { KeyId = StandInProvider.KeyId }),
                "unsigned" => c => Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""") + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(c)) + ".",
                _ => null,
            });

        Assert.Equal("/", location);
        Assert.Null(await MeAsync(browser));
        Assert.False(string.IsNullOrEmpty(await MessageAsync(browser)));
        Assert.Null(await MessageAsync(browser)); // shown once
        var failed = (await AuditAsync()).Last(a => a.Action == AuditActions.SignInFailed);
        Assert.Contains(reason, failed.Data, StringComparison.Ordinal);
        Assert.Contains(Method, failed.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePluginsRefusal_IsShownToThePerson()
    {
        await SetUpAsync();
        await SetAsync(new() { ["SignInDomain"] = "corp.example" });
        var browser = Browser();
        await SignInAsync(browser);
        Assert.Equal("Sign in with your account at corp.example.", await MessageAsync(browser));
        // At the right domain, the same check lets them on.
        var right = Browser();
        await SignInAsync(right, c => c["email"] = "ann@corp.example");
        Assert.NotNull(await MeAsync(right));
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    [InlineData("evil.example")]
    public async Task TheReturnPath_IsOnlyEverAPathOnThisSite(string returnTo)
    {
        await SetUpAsync();
        var browser = Browser();
        Assert.Equal("/", await SignInAsync(browser, returnTo: returnTo));
        Assert.NotNull(await MeAsync(browser));
    }

    [Fact]
    public async Task ACallback_WithoutTheStartsCookie_IsRefused()
    {
        await SetUpAsync();
        var r = await Browser().GetAsync(Callback + "?code=x&state=y", Ct);
        Assert.Equal("/", r.Headers.Location!.ToString());
        Assert.Contains("no sign-in in progress", (await AuditAsync()).Last().Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACodeIsUsedOnce_AndTheStateCookieToo()
    {
        await SetUpAsync();
        var browser = Browser();
        await SignInAsync(browser, back: q => { _replay = q; return q; });
        var again = await browser.GetAsync(Back(_replay!), Ct);
        Assert.Equal("/", again.Headers.Location!.ToString());
    }

    private Dictionary<string, string>? _replay;

    [Theory]
    [InlineData("/spaces/a?b=c#d", "/spaces/a?b=c#d")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("//evil.example", "/")]
    [InlineData(@"/\evil.example", "/")]
    [InlineData(@"/a\b", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/a\nb", "/")]
    public void LocalPath_KeepsAPathOnThisSite_AndNothingElse(string? given, string kept) =>
        Assert.Equal(kept, SignInMethods.LocalPath(given));

    [Fact]
    public void TheCodeChallenge_IsRfc7636s() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", SignInMethods.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public async Task ALocalSignIn_AndTheBreakGlassAccount_KeepWorking_WithAMethodSetUp()
    {
        await SetUpAsync();
        Assert.NotNull(await MeAsync(await _f.SignedInBreakGlassAsync()));
    }

    /// <summary>
    /// The provider at https://id.example.com: discovery, keys, and a token endpoint that checks the code, the PKCE verifier, the
    /// redirect address and the client's Basic credentials. The ID token is made from the authorization request's nonce.
    /// </summary>
    private sealed class StandInProvider : HttpMessageHandler
    {
        public const string Issuer = "https://id.example.com", ClientId = "client-1", Secret = "secret-1", KeyId = "key-1";
        private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = KeyId };
        private readonly Dictionary<string, (string Challenge, string Redirect, string Token)> _codes = [];
        public bool VerifierFitted { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastCode { get; private set; }

        public string Authorize(Dictionary<string, string> ask, Action<Dictionary<string, object>>? change, Func<Dictionary<string, object>, string>? make)
        {
            Assert.Equal(("code", ClientId, "S256"), (ask["response_type"], ask["client_id"], ask["code_challenge_method"]));
            Assert.Contains("openid", ask["scope"].Split(' '));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object>
            {
                ["iss"] = Issuer, ["aud"] = ClientId, ["sub"] = "person-1", ["email"] = "ann@example.org", ["email_verified"] = true, ["name"] = "Ann Example",
                ["nonce"] = ask["nonce"], ["iat"] = now, ["exp"] = now + 300,
            };
            change?.Invoke(claims);
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _codes[code] = (ask["code_challenge"], ask["redirect_uri"], make is null ? Sign(claims, Key) : make(claims));
            return LastCode = code;
        }

        public static string Sign(Dictionary<string, object> claims, SecurityKey key) =>
            new JsonWebTokenHandler().CreateToken(JsonSerializer.Serialize(claims), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("id.example.com", request.RequestUri!.Host);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    return Json(new { issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/keys" });
                case "/keys":
                    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(Key);
                    return Json(new { keys = new[] { new { kty = "RSA", kid = KeyId, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
                case "/token":
                    LastAuthorization = request.Headers.Authorization?.ToString();
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
                    if (form["grant_type"] != "authorization_code" || !_codes.Remove(form["code"].ToString(), out var issued)
                        || LastAuthorization != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{Secret}")) || form["redirect_uri"] != issued.Redirect)
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
                    VerifierFitted = SignInMethods.Challenge(form["code_verifier"].ToString()) == issued.Challenge;
                    if (!VerifierFitted) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
                    return Json(new { id_token = issued.Token, access_token = "at", token_type = "Bearer" });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
