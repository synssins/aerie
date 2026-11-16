using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Kb.Tests.Integration.DataCenter.DcKit;
using static Kb.Tests.Integration.DataCenter.OAuthKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Identity slice 10: the checks across slices 1 to 9 (identity-plan.md, Slice 10: hardening pass).</summary>
public sealed partial class OAuthHardeningTests
{
    /// <summary>Every line any logger wrote, at every level.</summary>
    private sealed class Captured : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);
        public void Dispose() { }

        private sealed class Writer(Captured to, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                to.Lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }

    [GeneratedRegex("kb[tor]_[A-Za-z0-9]{9,}")]
    private static partial Regex LongerThanAPrefix();

    // auth.audit.no_secrets: a whole flow with every kind of credential, and no secret, code or token in the audit log or the application log.
    [Fact]
    public async Task NoSecret_ReachesTheAuditLog_OrTheApplicationLog()
    {
        var log = new Captured();
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.AddSingleton<ILoggerProvider>(log));
        var admin = await f.SignedInBreakGlassAsync();
        var app = await ClientAsync(admin);
        var secrets = new List<string> { app.Secret! };
        var code = await CodeAsync(f, app, "READ", "ravi");
        secrets.Add(code);
        var t = await PostTokenAsync(f, app, new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Cb, ["code_verifier"] = Verifier });
        secrets.Add(t.GetProperty("access_token").GetString()!);
        secrets.Add(t.GetProperty("refresh_token").GetString()!);
        // The same code again, a refresh, reuse of the old refresh token, a revoke, bad secrets and tokens: every path that refuses something.
        await PostTokenAsync(f, app, new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Cb, ["code_verifier"] = Verifier }, HttpStatusCode.BadRequest);
        var t2 = await TokensAsync(f, app, "READ", "olga");
        var t3 = await PostTokenAsync(f, app, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = t2.GetProperty("refresh_token").GetString()! });
        await PostTokenAsync(f, app, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = t2.GetProperty("refresh_token").GetString()! }, HttpStatusCode.BadRequest);
        secrets.AddRange([t2.GetProperty("access_token").GetString()!, t2.GetProperty("refresh_token").GetString()!, t3.GetProperty("access_token").GetString()!, t3.GetProperty("refresh_token").GetString()!]);
        await Bearer(f, t3.GetProperty("access_token").GetString()!).GetAsync("/rest/api/user/current", Ct);
        await Bearer(f, t3.GetProperty("access_token").GetString()!).PostJsonAsync("/rest/api/space", new { key = "NO", name = "No" }); // scope refused
        var personal = (await (await admin.PostAsJsonAsync("/api/v1/tokens", new { name = "Log", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 1 }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
        secrets.Add(personal);
        await Bearer(f, personal).GetAsync("/rest/api/user/current", Ct);
        await Basic(f, "ravi", "a-wrong-password-9876").GetAsync("/rest/api/user/current", Ct);
        secrets.Add("a-wrong-password-9876");
        await Basic(f, "ravi", t3.GetProperty("access_token").GetString()!).GetAsync("/rest/api/user/current", Ct);
        var wrong = new HttpRequestMessage(HttpMethod.Post, "/rest/oauth2/latest/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = "x" }) };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:not-the-secret-123")));
        await f.NewClient().SendAsync(wrong, Ct);
        secrets.Add("not-the-secret-123");
        await f.NewClient().PostAsync("/rest/oauth2/latest/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = app.ClientId, ["client_secret"] = app.Secret!, ["token"] = t3.GetProperty("refresh_token").GetString()! }), Ct);
        await Bearer(f, t3.GetProperty("access_token").GetString()!).GetAsync("/rest/api/user/current", Ct); // revoked now
        var rotated = (await (await admin.PostAsync($"/api/v1/admin/oauth/clients/{app.Id}/secret", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("clientSecret").GetString()!;
        secrets.Add(rotated);

        var audit = string.Join('\n', await f.WithDbValueAsync(db => db.AuditEntries.Select(a => a.DataJson + " " + a.TargetId).ToListAsync(Ct)));
        var logs = string.Join('\n', log.Lines);
        Assert.NotEmpty(log.Lines); // the log was read
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
        // Prefixes are fine; nothing longer than a prefix shown (kbt_ / kbo_ / kbr_ plus 8 characters) is anywhere.
        Assert.Empty(LongerThanAPrefix().Matches(audit));
        // Each identity entry names the source address (identity.md, Audit); the client id is in every oauth.* entry.
        var entries = await f.WithDbValueAsync(db => db.AuditEntries.Where(a => a.Action.StartsWith("oauth.") || a.Action == AuditActions.BasicRefused || a.Action == AuditActions.TokenUseDenied)
            .Select(a => new { a.Action, a.DataJson }).ToListAsync(Ct));
        Assert.Contains(entries, e => e.Action == AuditActions.OAuthClientCreate);
        Assert.Contains(entries, e => e.Action == AuditActions.OAuthTokenReuse);
        Assert.All(entries, e => Assert.True(JsonDocument.Parse(e.DataJson).RootElement.TryGetProperty("ip", out _), e.Action + " has no ip"));
        Assert.All(entries.Where(e => e.Action.StartsWith("oauth.", StringComparison.Ordinal)), e => Assert.True(JsonDocument.Parse(e.DataJson).RootElement.TryGetProperty("clientId", out _), e.Action + " has no clientId"));
        Assert.Empty(LongerThanAPrefix().Matches(logs));
    }

    /// <summary>Every audit action identity.md lists exists under that name.</summary>
    [Fact]
    public void EveryAuditActionOfIdentity_Exists()
    {
        var names = typeof(AuditActions).GetFields().Where(x => x.IsLiteral).Select(x => (string)x.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     "oauth.client.create", "oauth.client.update", "oauth.client.secret.rotate", "oauth.client.delete", "oauth.consent.grant", "oauth.consent.deny",
                     "oauth.grant.revoke", "oauth.token.issue", "oauth.token.refresh", "oauth.token.reuse", "auth.basic.refused", "token.use.denied",
                 })
            Assert.Contains(name, names);
    }

    // The limits on authorize and revoke (token and Basic are in OAuthTokenTests and DcLabFindingsTests).
    [Fact]
    public async Task AuthorizeAndRevoke_AreLimited()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:SignInsPerMinutePerAddress"] = "3", ["Kb:Auth:OAuthTokenRequestsPerMinute"] = "3" });
        var b = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.BadRequest, (await b.GetAsync("/rest/oauth2/latest/authorize?client_id=nope", Ct)).StatusCode);
        var limited = await b.GetAsync("/rest/oauth2/latest/authorize?client_id=nope", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        for (var i = 0; i < 3; i++) await f.NewClient().PostAsync("/rest/oauth2/latest/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "c" + i, ["token"] = "x" }), Ct);
        var revoke = await f.NewClient().PostAsync("/rest/oauth2/latest/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "c9", ["token"] = "x" }), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, revoke.StatusCode);
        Assert.Equal("temporarily_unavailable", (await revoke.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.NotNull(revoke.Headers.RetryAfter);
    }

    /// <summary>In a production install on plain http, OAuth refuses to work unless the test-install setting allows plain http (L-0100).</summary>
    [Fact]
    public async Task OnPlainHttp_InProduction_OAuthRefuses()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), environment: "Production", settings: new Dictionary<string, string> { ["Kb:DataCenter:BaseUrl"] = "http://wiki.example" });
        var admin = await f.SignedInBreakGlassAsync();
        var app = await ClientAsync(admin);
        var b = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var r = await b.GetAsync($"/rest/oauth2/latest/authorize?response_type=code&client_id={app.ClientId}&redirect_uri={Uri.EscapeDataString(Cb)}&scope=READ", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("https", await r.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var token = await f.NewClient().PostAsync("/rest/oauth2/latest/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = app.ClientId, ["client_secret"] = app.Secret! }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.Equal("invalid_request", (await token.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        // The test-install setting lets it work on plain http.
        (await admin.PutAsJsonAsync("/api/v1/admin/auth/compat", new { basicPasswordEnabled = true, allowHttpRedirects = true, codeSeconds = 300, accessTokenSeconds = 3600, refreshTokenDays = 90 }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync($"/rest/oauth2/latest/authorize?response_type=code&client_id={app.ClientId}&redirect_uri={Uri.EscapeDataString(Cb)}&scope=READ", Ct)).StatusCode);
    }
}
