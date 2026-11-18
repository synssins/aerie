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

    private readonly List<string> _clients = [];

    public SignInMethodTests() =>
        _f = new(KbAppFactory.NewDbPath(), services: s =>
        {
            s.AddHttpClient(PluginHttp.ClientName("kb.sample")).ConfigurePrimaryHttpMessageHandler(() => _provider);
            // The clients of the providers an Admin adds: the stand-in, and a note of which of the two was used.
            foreach (var name in new[] { SignInProviderHttp.Public, SignInProviderHttp.Private })
                s.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new Recorder(name, _clients, _provider));
        });

    /// <summary>Notes which client was used, then hands the request to the stand-in, which it does not dispose: every client shares it.</summary>
    private sealed class Recorder(string name, List<string> used, HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _to = new(inner, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (used) used.Add(name);
            return _to.SendAsync(request, ct);
        }
    }

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
        Func<Dictionary<string, string>, Dictionary<string, string>>? back = null, Func<Dictionary<string, object>, string>? token = null, string method = Method)
    {
        var start = await browser.GetAsync($"/api/v1/auth/methods/{method}/start" + (returnTo is null ? "" : "?returnTo=" + Uri.EscapeDataString(returnTo)), Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var at = start.Headers.Location!;
        Assert.StartsWith("https://id.example.com/authorize?", at.ToString(), StringComparison.Ordinal);
        var ask = QueryHelpers.ParseQuery(at.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var code = _provider.Authorize(ask, claims, token);
        var query = new Dictionary<string, string> { ["code"] = code, ["state"] = ask["state"] };
        if (back is not null) query = back(query);
        var end = await browser.GetAsync($"/api/v1/auth/methods/{method}/callback?" + string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))), Ct);
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
    }

    /// <summary>A linked account is entered through its method only: the link takes its password and every password link made before it.</summary>
    [Fact]
    public async Task ALinkedAccount_HasNoOtherWayIn()
    {
        await SetUpAsync(newAccounts: false);
        var local = await LocalUserAsync("ann", "ann@example.org");
        var before = (await (await _admin.PostAsync($"/api/v1/admin/users/{local.Id}/password-link", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setPasswordToken").GetString();
        await SignInAsync(Browser());
        await _f.WithDbAsync(async db => Assert.Null((await db.Users.SingleAsync(x => x.Id == local.Id, Ct)).PasswordHash));

        // The password link made before the link is dead, and no new one is made.
        var set = await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/password", new { userId = local.Id, token = before, password = "a-new-password-0123456789" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, set.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsync($"/api/v1/admin/users/{local.Id}/password-link", null, Ct)).StatusCode);
        // The old password opens nothing: the sign-in page and Basic answer as for a wrong password.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "ann", password = "user-password-0123456789" }, Ct)).StatusCode);
        // The method still lets the person in.
        var again = Browser();
        await SignInAsync(again);
        Assert.Equal("ann", (await MeAsync(again))!.Value.GetProperty("userName").GetString());
    }

    /// <summary>An account linked before the rule kept its password: it still opens nothing, on the sign-in page or with Basic.</summary>
    [Fact]
    public async Task AnAccountLinkedWithAPasswordLeft_IsRefusedAsForAWrongPassword()
    {
        var local = await LocalUserAsync("old", "old@example.org");
        await _f.WithDbAsync(async db => { var u = await db.Users.SingleAsync(x => x.Id == local.Id, Ct); (u.ExternalIssuer, u.ExternalId) = (StandInProvider.Issuer, "person-9"); await db.SaveChangesAsync(Ct); });

        var r = await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "old", password = "user-password-0123456789" }, Ct);
        var wrong = await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "old", password = "not-the-password-0123456" }, Ct);
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (r.StatusCode, wrong.StatusCode));
        static async Task<string?> Title(HttpResponseMessage m) => (await m.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString();
        Assert.Equal(await Title(wrong), await Title(r));

        var basic = _f.NewClient();
        basic.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("old:user-password-0123456789")));
        await basic.GetAsync("/rest/api/space", Ct);
        await _f.WithDbAsync(async db => Assert.Contains(await db.AuditEntries.Where(a => a.Action == AuditActions.BasicRefused).Select(a => a.DataJson).ToListAsync(Ct),
            d => d.Contains("linked to a sign-in method", StringComparison.Ordinal)));
        await _f.WithDbAsync(async db => Assert.Contains(await db.AuditEntries.Where(a => a.Action == AuditActions.SignInFailed).Select(a => a.DataJson).ToListAsync(Ct),
            d => d.Contains("linked to a sign-in method", StringComparison.Ordinal)));
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

    // ---- providers an Admin adds (contract 0.9.0)

    private async Task<string> AddProviderAsync(string title = "Lab", Dictionary<string, string>? more = null)
    {
        var r = await _admin.PostAsJsonAsync("/api/v1/admin/plugins/kb.sample/signin-providers", new { title }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var values = new Dictionary<string, string>
        {
            [$"signIn.{id}.issuer"] = StandInProvider.Issuer, [$"signIn.{id}.clientId"] = StandInProvider.ClientId,
            [$"signIn.{id}.clientSecret"] = StandInProvider.Secret, [$"signIn.{id}.newAccounts"] = "true",
        };
        foreach (var (k, v) in more ?? []) values[$"signIn.{id}.{k}"] = v;
        await SetAsync(values);
        return id;
    }

    private async Task<List<string>> ButtonsAsync() =>
        [.. (await _f.NewClient().GetFromJsonAsync<JsonElement>("/api/v1/auth/methods", Ct)).EnumerateArray().Select(m => m.GetProperty("id").GetString() + " " + m.GetProperty("name").GetString())];

    private async Task<JsonElement> SampleMethodAsync(string key) =>
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/plugins", Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample")
            .GetProperty("signInMethods").EnumerateArray().Single(m => m.GetProperty("id").GetString() == key);

    [Fact]
    public async Task AProviderAnAdminAdds_IsAButtonOfItsOwn_SignsPeopleIn_AndKeepsItsAddressWhenRenamed()
    {
        var id = await AddProviderAsync();
        var key = "kb.sample/" + id;
        Assert.Contains(key + " Lab", await ButtonsAsync());
        var address = (await SampleMethodAsync(key)).GetProperty("redirectAddress").GetString();
        Assert.EndsWith($"/api/v1/auth/methods/kb.sample/{id}/callback", address, StringComparison.Ordinal);

        var browser = Browser();
        Assert.Equal("/spaces", await SignInAsync(browser, returnTo: "/spaces", method: key));
        Assert.Equal("ann@example.org", (await MeAsync(browser))!.Value.GetProperty("userName").GetString());
        Assert.True(_provider.VerifierFitted);
        Assert.True(_provider.UserInfoCalls > 0); // "Read userinfo" is on unless an Admin turns it off
        Assert.All(_clients, c => Assert.Equal(SignInProviderHttp.Public, c));

        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"/api/v1/admin/plugins/kb.sample/signin-providers/{id}", new { title = "Lab two" }, Ct)).StatusCode);
        Assert.Contains(key + " Lab two", await ButtonsAsync());
        Assert.Equal(address, (await SampleMethodAsync(key)).GetProperty("redirectAddress").GetString());
        var actions = (await AuditAsync()).Select(a => a.Action).ToList();
        await _f.WithDbAsync(async db => Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.Action.StartsWith("plugin.signin.provider."), Ct)));
        Assert.Contains("auth.method.start", actions);

    }

    [Fact]
    public async Task AProvider_WithoutAnIssuer_IsNoButton_AndOnlyAnAdminAddsOne()
    {
        var r = await _admin.PostAsJsonAsync("/api/v1/admin/plugins/kb.sample/signin-providers", new { title = "Half set up" }, Ct);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        await SetAsync(new() { [$"signIn.{id}.clientId"] = StandInProvider.ClientId });
        Assert.DoesNotContain(await ButtonsAsync(), b => b.StartsWith("kb.sample/" + id, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await Browser().GetAsync($"/api/v1/auth/methods/kb.sample/{id}/start", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/v1/admin/plugins/kb.sample/signin-providers", new { title = "" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task AProvidersRequiredClaim_LetsOnlyItsValuesIn_AndUserinfoFillsWhatTheIdTokenLacks()
    {
        // hd is only in the userinfo answer.
        var id = await AddProviderAsync(more: new() { ["requiredClaim"] = "hd", ["requiredValues"] = "other.org" });
        var key = "kb.sample/" + id;
        var refused = Browser();
        await SignInAsync(refused, method: key);
        Assert.Null(await MeAsync(refused));
        Assert.Equal("Your account at this provider is not allowed here. Ask an Administrator.", await MessageAsync(refused));
        Assert.Contains((await AuditAsync()).Where(a => a.Action == AuditActions.SignInFailed), a => a.Data.Contains("the required claim does not match", StringComparison.Ordinal));

        await SetAsync(new() { [$"signIn.{id}.requiredValues"] = "other.org\nexample.org" });
        var allowed = Browser();
        await SignInAsync(allowed, method: key);
        Assert.NotNull(await MeAsync(allowed));
    }

    [Fact]
    public async Task AUserinfoAnswer_ForAnotherSubject_RefusesTheSignIn()
    {
        _provider.UserInfo = new() { ["sub"] = "someone-else" };
        var key = "kb.sample/" + await AddProviderAsync();
        var browser = Browser();
        await SignInAsync(browser, method: key);
        Assert.Null(await MeAsync(browser));
        Assert.Contains((await AuditAsync()).Where(a => a.Action == AuditActions.SignInFailed), a => a.Data.Contains("another subject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePluginsOwnCheck_RunsOnItsProviders()
    {
        var key = "kb.sample/" + await AddProviderAsync();
        var browser = Browser();
        await SignInAsync(browser, c => c["test_account"] = true, method: key);
        Assert.Equal("Test accounts cannot sign in here.", await MessageAsync(browser));
    }

    [Fact]
    public async Task AProviderOnAPrivateNetwork_IsReachedWithThePrivateClient_OnlyWhenAnAdminSaysSo()
    {
        var key = "kb.sample/" + await AddProviderAsync(more: new() { ["privateNetwork"] = "true" });
        await SignInAsync(Browser(), method: key);
        Assert.Contains(SignInProviderHttp.Private, _clients);
        Assert.DoesNotContain(SignInProviderHttp.Public, _clients);
    }

    [Fact]
    public async Task RemovingAProvider_TakesItsButton_AndKeepsTheLinksOfItsAccounts()
    {
        var id = await AddProviderAsync();
        var key = "kb.sample/" + id;
        var browser = Browser();
        await SignInAsync(browser, method: key);
        Assert.Equal(1, (await SampleMethodAsync(key)).GetProperty("linked").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/plugins/kb.sample/signin-providers/{id}", Ct)).StatusCode);
        Assert.DoesNotContain(await ButtonsAsync(), b => b.StartsWith(key, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await Browser().GetAsync($"/api/v1/auth/methods/{key}/start", Ct)).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Users.CountAsync(u => u.ExternalIssuer == StandInProvider.Issuer && u.ExternalId == "person-1", Ct));
            Assert.False(await db.PluginSettingValues.AnyAsync(v => v.PluginId == "kb.sample" && v.Key.StartsWith($"signIn.{id}."), Ct)); // its secret too
            Assert.Contains("\"linkedAccounts\":\"1\"", (await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.SignInProviderRemove, Ct)).DataJson, StringComparison.Ordinal);
        });
    }

    /// <summary>A provider's clock a little ahead of this one: an issue time and a not-before time inside the clock skew are accepted.</summary>
    [Fact]
    public async Task AnIssueTimeOrNotBefore_JustAhead_WithinTheClockSkew_IsAccepted()
    {
        await SetUpAsync();
        var browser = Browser();
        var ahead = DateTimeOffset.UtcNow.Add(SignInFlow.ClockSkew / 2).ToUnixTimeSeconds();
        await SignInAsync(browser, c => (c["iat"], c["nbf"]) = (ahead, ahead));
        Assert.NotNull(await MeAsync(browser));
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
        { "issued-in-the-future", "issue time is in the future (iat)" },
        { "not-valid-yet", "not valid yet (nbf)" },
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
                // An issue time an hour ahead, with an expiry still later: everything else is right.
                "issued-in-the-future" => c => (c["iat"], c["exp"]) = (DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()),
                "not-valid-yet" => c => (c["nbf"], c["exp"]) = (DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()),
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
        public int UserInfoCalls { get; private set; }
        /// <summary>What the userinfo endpoint answers.</summary>
        public Dictionary<string, object> UserInfo { get; set; } = new() { ["sub"] = "person-1", ["hd"] = "example.org" };

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
                    return Json(new { issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/keys", userinfo_endpoint = Issuer + "/userinfo" });
                case "/userinfo":
                    lock (this) UserInfoCalls++;
                    return request.Headers.Authorization?.ToString() == "Bearer at" ? Json(UserInfo) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
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
