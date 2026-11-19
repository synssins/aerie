using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The Low findings of the code review of 2026-11-17 about signing in and sessions: L3 (the answer does not say whether an account
/// exists), L4 (a write from another site's page), L5 (a session ends, a code is good once, turning codes off needs a fresh sign-in).
/// </summary>
public sealed class SignInSessionTests
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[]}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A clock the test moves forward.</summary>
    private sealed class Clock : TimeProvider
    {
        public TimeSpan Ahead { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Ahead;
    }

    private static async Task<Guid> UserAsync(KbAppFactory f, string name)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient c, string name, string password = Password) =>
        c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password }, Ct);

    // ---- L3 ----

    [Fact]
    public async Task WithLocalSignInOff_AMadeUpNameGetsTheSameAnswerAsARealOne()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        await UserAsync(f, "real");
        await f.WithDbAsync(db => db.SetAsync(RuntimeSettingKeys.LocalSignInDisabled, "true", null, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Forbidden, (await SignInAsync(f.NewClient(), "real")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignInAsync(f.NewClient(), "nobody-by-this-name")).StatusCode);
        // The break-glass account is the way back in and still gets its own answer.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(f.NewClient(), "breakglass", "wrong-password-0123456789")).StatusCode);
    }

    // ---- L4 ----

    [Fact]
    public async Task AWriteWithTheSessionCookie_FromAnotherSitesPage_IsRefused()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        async Task<HttpStatusCode> PostAsync(HttpClient by, params (string Name, string Value)[] headers)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/spaces") { Content = JsonContent.Create(new { name = "S " + Guid.NewGuid().ToString("N")[..8] }) };
            foreach (var (name, value) in headers) req.Headers.Add(name, value);
            return (await by.SendAsync(req, Ct)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(admin, ("Sec-Fetch-Site", "cross-site")));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(admin, ("Sec-Fetch-Site", "same-site"))); // a sibling host of the same domain
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(admin, ("Origin", "https://files.example")));
        // The site's own pages, a browser that says nothing, and a script with no browser at all are served.
        Assert.Equal(HttpStatusCode.Created, await PostAsync(admin, ("Sec-Fetch-Site", "same-origin"), ("Origin", "https://localhost")));
        Assert.Equal(HttpStatusCode.Created, await PostAsync(admin, ("Origin", "http://localhost"))); // the scheme is the proxy's business
        Assert.Equal(HttpStatusCode.Created, await PostAsync(admin));
        // Reading is not a write, and sign-out of someone who is not signed in is nobody's session.
        var read = new HttpRequestMessage(HttpMethod.Get, "/api/v1/spaces");
        read.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(read, Ct)).StatusCode);

        // A token is sent on purpose, never by a browser on its own: the rule is not for it.
        var token = (await (await admin.PostAsJsonAsync("/api/v1/tokens", new { name = "T", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Write" } }, days = 1 }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Tokens" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var bearer = f.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var byToken = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/spaces/{space}/pages") { Content = JsonContent.Create(new { kind = "Page", title = "By token", contentJson = Doc }) };
        byToken.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Created, (await bearer.SendAsync(byToken, Ct)).StatusCode);
    }

    // ---- L7 ----

    [Fact]
    public async Task OwnPassword_IsChangedWithTheCurrentOne_AndEndsTheOtherSessions()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var id = await UserAsync(f, "pia");
        var (here, elsewhere) = (f.NewClient(), f.NewClient());
        (await SignInAsync(here, "pia")).EnsureSuccessStatusCode();
        (await SignInAsync(elsewhere, "pia")).EnsureSuccessStatusCode();
        const string next = "a-much-better-passphrase-2026";

        Assert.Equal(HttpStatusCode.BadRequest, (await here.PostAsJsonAsync("/api/v1/me/password", new { current = "not-my-password-0123456789", password = next }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await here.PostAsJsonAsync("/api/v1/me/password", new { current = Password, password = "short" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await here.PostAsJsonAsync("/api/v1/me/password", new { current = Password, password = next }, Ct)).StatusCode);

        // This session stays, the other one ends, and the new password is the one that signs in.
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(f.NewClient(), "pia")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(f.NewClient(), "pia", next)).StatusCode);
        await f.WithDbAsync(async db => Assert.Contains("self", (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.AuditEntries, a => a.Action == Kb.Core.Audit.AuditActions.PasswordSet, Ct)).DataJson, StringComparison.Ordinal));

        // A shared account (exempt from codes by an Admin, such as a public demo account) cannot have its password changed by one of its users.
        await f.WithDbAsync(async db => { (await db.Users.FindAsync([id], Ct))!.CodesExempt = true; await db.SaveChangesAsync(Ct); });
        Assert.Equal(HttpStatusCode.Conflict, (await here.PostAsJsonAsync("/api/v1/me/password", new { current = next, password = "another-good-passphrase-2026" }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaa")]
    [InlineData("abcabcabcabcabcabc")]
    [InlineData("1234567890123456")]
    [InlineData("qwertyuiopasdfgh")]
    [InlineData("PasswordPassword")]
    [InlineData("piapiapiapiapia12")]
    public async Task ALongPasswordThatIsEasyToGuess_IsRefused(string weak)
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        await UserAsync(f, "pia");
        var c = f.NewClient();
        (await SignInAsync(c, "pia")).EnsureSuccessStatusCode();
        var r = await c.PostAsJsonAsync("/api/v1/me/password", new { current = Password, password = weak }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("too easy to guess", await r.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>The same rule on the compatible surface, where the surface's own XSRF header is asked for as well, and on nothing that a token or a password makes.</summary>
    [Fact]
    public async Task AWriteWithTheSessionCookie_FromAnotherSitesPage_IsRefusedOnTheCompatibleSurfaceToo()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).EnsureSuccessStatusCode();
        var n = 0;
        async Task<HttpResponseMessage> PostAsync(HttpClient by, params (string Name, string Value)[] headers)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/rest/api/content")
            {
                Content = JsonContent.Create(new { type = "page", title = "Page " + ++n, space = new { key = "OPS" }, body = new { storage = new { value = "<p>x</p>", representation = "storage" } } }),
            };
            req.Headers.Add(Kb.Web.DataCenter.DcRest.XsrfHeader, "no-check");
            foreach (var (name, value) in headers) req.Headers.Add(name, value);
            return await by.SendAsync(req, Ct);
        }
        foreach (var header in new[] { ("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Site", "same-site"), ("Origin", "https://evil.example") })
        {
            var refused = await PostAsync(admin, header);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            // The surface's own error body, like every other refusal there.
            Assert.Equal(403, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("statusCode").GetInt32());
        }
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(admin, ("Sec-Fetch-Site", "same-origin"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(admin)).StatusCode);

        // A caller who sends a password or a token is not a browser acting on a cookie: the rule is not for them.
        var cora = (await UserAsync(f, "cora")).ToString();
        var space = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/spaces", Ct)).EnumerateArray().First().GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = cora, role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var basic = f.NewClient();
        basic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("cora:" + Password)));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(basic, ("Sec-Fetch-Site", "cross-site"))).StatusCode);

        // The OAuth token endpoint is called by applications from anywhere and never acts on the cookie: a cookie that comes along changes nothing.
        var token = new HttpRequestMessage(HttpMethod.Post, "/rest/oauth2/latest/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code" }) };
        token.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await admin.SendAsync(token, Ct)).StatusCode);
    }

    // ---- L5 ----

    private const string CookieName = "__Host-kb";

    /// <summary>The session cookie a response sets, as "name=value"; null when it sets none or clears it.</summary>
    private static string? SessionCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var set)
            ? set.Select(c => c.Split(';')[0]).LastOrDefault(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal) && c.Length > CookieName.Length + 1)
            : null;

    /// <summary>A request that carries exactly this cookie, as a browser that kept it would.</summary>
    private static async Task<HttpResponseMessage> WithAsync(HttpClient bare, string cookie, HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("Cookie", cookie);
        return await bare.SendAsync(req, Ct);
    }

    private static (KbAppFactory F, Clock Clock, HttpClient Bare) HourLongSessions()
    {
        var clock = new Clock();
        var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:SessionMaxAge"] = "01:00:00" },
            services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        // No cookie jar: each request says which cookie it carries.
        return (f, clock, f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false }));
    }

    /// <summary>
    /// The start is fixed at sign-in. The cookie from sign-in and every cookie the app sets on the way are all refused once the most a
    /// session lasts has passed since sign-in, however often the session was used.
    /// </summary>
    [Fact]
    public async Task ASession_EndsAFixedTimeAfterSignIn_WhicheverOfItsCookiesIsUsed()
    {
        var (f, clock, bare) = HourLongSessions();
        await using var _ = f;
        await UserAsync(f, "uma");
        var first = SessionCookie(await SignInAsync(bare, "uma"))!;
        var latest = first;
        // Used every few minutes with the cookie from sign-in and with the newest one: the sliding expiry alone would never end it.
        foreach (var minutes in new[] { 1, 4, 4, 4, 13, 13, 10, 9 }) // at 1, 5, 9, 13, 26, 39, 49 and 58 minutes
        {
            clock.Ahead += TimeSpan.FromMinutes(minutes);
            Assert.Equal(HttpStatusCode.OK, (await WithAsync(bare, first, HttpMethod.Get, "/api/v1/me")).StatusCode);
            var r = await WithAsync(bare, latest, HttpMethod.Get, "/api/v1/me");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            latest = SessionCookie(r) ?? latest;
        }
        clock.Ahead += TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(45)); // 60 minutes 45 seconds after sign-in
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithAsync(bare, first, HttpMethod.Get, "/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithAsync(bare, latest, HttpMethod.Get, "/api/v1/me")).StatusCode);
    }

    /// <summary>The app signs someone in again when they change their password. That continues the session: it does not start its time anew.</summary>
    [Fact]
    public async Task SigningInAgainFromInsideASession_DoesNotMakeItLastLonger()
    {
        var (f, clock, bare) = HourLongSessions();
        await using var _ = f;
        await UserAsync(f, "pia");
        var first = SessionCookie(await SignInAsync(bare, "pia"))!;
        clock.Ahead = TimeSpan.FromMinutes(50);
        var changed = await WithAsync(bare, first, HttpMethod.Post, "/api/v1/me/password", new { current = Password, password = "a-much-better-passphrase-2026" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var again = SessionCookie(changed)!;
        Assert.Equal(HttpStatusCode.OK, (await WithAsync(bare, again, HttpMethod.Get, "/api/v1/me")).StatusCode);
        clock.Ahead = TimeSpan.FromMinutes(61);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithAsync(bare, again, HttpMethod.Get, "/api/v1/me")).StatusCode);
        // A sign-in of its own is a new session with its own hour.
        var fresh = SessionCookie(await SignInAsync(bare, "pia", "a-much-better-passphrase-2026"))!;
        clock.Ahead = TimeSpan.FromMinutes(110);
        Assert.Equal(HttpStatusCode.OK, (await WithAsync(bare, fresh, HttpMethod.Get, "/api/v1/me")).StatusCode);
        // So is a sign-in with the password from a browser that still holds a session's cookie: it is a new session, not the old one's time.
        var over = SessionCookie(await WithAsync(bare, fresh, HttpMethod.Post, "/api/v1/auth/local/signin", new { userName = "pia", password = "a-much-better-passphrase-2026" }))!;
        clock.Ahead = TimeSpan.FromMinutes(165);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithAsync(bare, fresh, HttpMethod.Get, "/api/v1/me")).StatusCode); // 104 minutes old
        Assert.Equal(HttpStatusCode.OK, (await WithAsync(bare, over, HttpMethod.Get, "/api/v1/me")).StatusCode); // 55 minutes old
    }

    /// <summary>
    /// A cookie from before the start was written into cookies (a session open when the site is updated) has none. Its start is the
    /// time that cookie was issued: it is neither ended early nor made to last for ever by being used.
    /// </summary>
    [Fact]
    public async Task ACookieWithoutAStart_EndsAFixedTimeAfterItWasIssued()
    {
        var (f, clock, bare) = HourLongSessions();
        await using var _ = f;
        var id = await UserAsync(f, "uma");
        string old;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<KbUser>>();
            var principal = await signIn.CreateUserPrincipalAsync((await signIn.UserManager.FindByIdAsync(id.ToString()))!);
            var issued = clock.GetUtcNow().AddMinutes(-50);
            var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal,
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IssuedUtc = issued, ExpiresUtc = issued.AddHours(8) }, IdentityConstants.ApplicationScheme);
            var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            old = CookieName + "=" + options.TicketDataFormat.Protect(ticket);
        }
        // Issued 50 minutes ago: not signed out by the update.
        var r = await WithAsync(bare, old, HttpMethod.Get, "/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var renewed = SessionCookie(r)!; // the start is written into the cookie the app hands back
        clock.Ahead = TimeSpan.FromMinutes(9);
        Assert.Equal(HttpStatusCode.OK, (await WithAsync(bare, old, HttpMethod.Get, "/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WithAsync(bare, renewed, HttpMethod.Get, "/api/v1/me")).StatusCode);
        // An hour after it was issued, used or not, old cookie or new: over.
        clock.Ahead = TimeSpan.FromMinutes(11);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithAsync(bare, old, HttpMethod.Get, "/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithAsync(bare, renewed, HttpMethod.Get, "/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task ACodeFromTheApp_IsGoodOnce()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        await UserAsync(f, "gus");
        var first = f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(first, "gus")).StatusCode);
        var key = (await (await first.PostAsync("/api/v1/auth/codes/key", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct)).StatusCode);

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var code = Codes.Totp(key, step);
        var second = f.NewClient();
        Assert.Equal(HttpStatusCode.Conflict, (await SignInAsync(second, "gus")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/api/v1/auth/local/code", new { code }, Ct)).StatusCode);

        // Someone who saw that code typed has the password too: the same code does not open a second session.
        var third = f.NewClient();
        Assert.Equal(HttpStatusCode.Conflict, (await SignInAsync(third, "gus")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await third.PostAsJsonAsync("/api/v1/auth/local/code", new { code }, Ct)).StatusCode);
        // The next code is taken.
        Assert.Equal(HttpStatusCode.OK, (await third.PostAsJsonAsync("/api/v1/auth/local/code", new { code = Codes.Totp(key, step + 1) }, Ct)).StatusCode);
    }

    [Fact]
    public async Task TurningCodesOff_NeedsASignInMadeJustNow()
    {
        var clock = new Clock();
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        await UserAsync(f, "gus");
        var c = f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(c, "gus")).StatusCode);
        var key = (await (await c.PostAsync("/api/v1/auth/codes/key", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/codes", new { code = Codes.Totp(key) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me", Ct)).StatusCode);

        // The session has been open for a while: whoever is at the keyboard now cannot take the codes away.
        clock.Ahead = TimeSpan.FromMinutes(11);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync("/api/v1/me/codes", Ct)).StatusCode);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/v1/me/codes", Ct)).GetProperty("on").GetBoolean());

        // Signed in again, password and code, it works.
        clock.Ahead = TimeSpan.Zero;
        var fresh = f.NewClient();
        (await Codes.SignInAsync(f, fresh, "gus", Password)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await fresh.DeleteAsync("/api/v1/me/codes", Ct)).StatusCode);
    }
}
