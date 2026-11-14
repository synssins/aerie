using System.Net;
using System.Net.Http.Json;
using Kb.Core.Audit;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web;
using Kb.Web.Plugins;
using Kb.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kb.Tests.Integration;

/// <summary>App on a throwaway SQLite file. Two factories given the same path behave like two load-balanced instances.</summary>
public sealed class KbAppFactory(string dbPath, bool breakGlass = true, bool loopback = false, IDictionary<string, string>? settings = null, Action<IServiceCollection>? services = null,
    string? workFolder = null) : WebApplicationFactory<Program>
{
    public const string BreakGlassPassword = "test-breakglass-password-0123456789";
    public string DbPath { get; } = dbPath;
    public string SetupCodeFile { get; } = dbPath + ".setup-code.txt";
    /// <summary>Where this instance unpacks plugins. A restarted instance keeps it; a second instance has its own.</summary>
    public string WorkFolder { get; } = workFolder ?? dbPath + ".plugins";
    /// <summary>Records the restarts the app asked for.</summary>
    public FakeRestarter Restarter { get; } = new();

    public static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"kb-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Kb:Database:Provider", "Sqlite");
        builder.UseSetting("Kb:Database:ConnectionString", $"Data Source={DbPath}");
        builder.UseSetting("Kb:Setup:CodeFile", SetupCodeFile);
        builder.UseSetting("Kb:Logs:Folder", DbPath + ".logs");
        builder.UseSetting("Kb:Auth:SignInsPerMinutePerAddress", "100000");
        builder.UseSetting("Kb:Auth:AnonymousRequestsPerMinutePerAddress", "100000");
        if (breakGlass) builder.UseSetting("Kb--BreakGlassPassword", BreakGlassPassword);
        foreach (var (k, v) in settings ?? new Dictionary<string, string>()) builder.UseSetting(k, v);
        // TestServer has no remote address; loopback=true makes requests look like they come from the server itself.
        if (loopback) builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, LoopbackFilter>());
        builder.UseSetting("Kb:Plugins:WorkFolder", WorkFolder);
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IAppRestarter>(Restarter)));
        if (services is not null) builder.ConfigureServices(services);
    }

    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Loopback; return n(ctx); });
            next(app);
        };
    }

    /// <summary>Stops this instance and starts a new one on the same database and work folder: what a restart of the app does.</summary>
    public async Task<KbAppFactory> RestartAsync()
    {
        await DisposeAsync();
        return new KbAppFactory(DbPath, breakGlass, loopback, settings, services, WorkFolder);
    }

    /// <summary>Another server: the same database, its own work folder.</summary>
    public KbAppFactory SecondInstance() => new(DbPath, breakGlass, loopback, settings, services, $"{DbPath}.plugins-{Guid.NewGuid():N}");

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

    public async Task<HttpClient> SignedInBreakGlassAsync()
    {
        var c = NewClient();
        var r = await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "breakglass", password = BreakGlassPassword });
        r.EnsureSuccessStatusCode();
        return c;
    }

    public async Task WithDbAsync(Func<KbDbContext, Task> f)
    {
        await using var scope = Services.CreateAsyncScope();
        await f(scope.ServiceProvider.GetRequiredService<KbDbContext>());
    }
}

public sealed class ApiTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task Live_Returns200_Anonymously()
    {
        var r = await _f.NewClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns200_WhenDbReachable()
    {
        var r = await _f.NewClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task UnknownApiRoute_Returns401_WhenAnonymous()
    {
        var r = await _f.NewClient().GetAsync("/api/v1/anything", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task SecurityHeaders_AndVersionHeader_Present()
    {
        var r = await _f.NewClient().GetAsync("/api/v1/brand", TestContext.Current.CancellationToken);
        // Opaque change marker only: never the build number or git commit.
        Assert.Matches("^[0-9A-F]{12}$", r.Headers.GetValues("X-Kb-Version").Single());
        Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("same-origin", r.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Contains("camera=()", r.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task BreakGlass_SignIn_WritesAudit_AndNotifiesAdmins()
    {
        var c = await _f.SignedInBreakGlassAsync();
        await _f.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BreakGlass));
            Assert.True(await db.Notifications.AnyAsync(n => n.Title == "Break-glass account signed in"));
        });
        var count = await c.GetFromJsonAsync<Dictionary<string, int>>("/api/v1/notifications/count", TestContext.Current.CancellationToken);
        Assert.True(count!["unread"] >= 1);
    }

    [Fact]
    public async Task WrongPassword_Returns401_AndAuditsFailure()
    {
        var r = await _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "breakglass", password = "nope" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SignInFailed)));
    }

    [Fact]
    public async Task SelfRegistration_RouteDoesNotExist()
    {
        var c = await _f.SignedInBreakGlassAsync();
        var r = await c.PostAsJsonAsync("/api/v1/auth/register", new { userName = "x" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GetSpace_NoRole_Returns404NotForbidden()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var created = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "hr", name = "HR", isSensitive = true }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SpaceDto>(TestContext.Current.CancellationToken);

        // Admin has no silent access to a sensitive space: indistinguishable from "does not exist".
        var r = await admin.GetAsync($"/api/v1/spaces/{created!.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var list = await admin.GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(list!, s => s.Id == created.Id);
    }

    [Fact]
    public async Task EmergencyAccess_OpensSensitiveSpace_AndAudits()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var hr = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "legal", name = "Legal", isSensitive = true }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SpaceDto>(TestContext.Current.CancellationToken);
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{hr!.Id}/emergency-access", new { reason = "SSO outage investigation" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/spaces/{hr.Id}", TestContext.Current.CancellationToken)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.EmergencyAccess)));
    }

    [Fact]
    public async Task LocalDisable_BeforeExternalAdminSignIn_Returns409()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var r = await admin.PostAsync("/api/v1/admin/auth/local/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Plugins_ListsFirstPartyEmailPlugins()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var body = await admin.GetStringAsync("/api/v1/admin/plugins", TestContext.Current.CancellationToken);
        Assert.Contains("kb.email.smtp", body);
        Assert.Contains("kb.email.graph", body);
    }

    [Fact]
    public async Task Audit_Verify_DetectsTampering()
    {
        await _f.SignedInBreakGlassAsync(); // writes signin + breakglass entries
        await _f.WithDbAsync(async db =>
        {
            var audit = new SqlAuditLog(db, TimeProvider.System, [], NSubstitute.Substitute.For<Kb.Plugins.IPluginFailures>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlAuditLog>.Instance);
            await audit.AppendAsync(new AuditEvent("test.one", null), TestContext.Current.CancellationToken);
            Assert.True((await audit.VerifyAsync(TestContext.Current.CancellationToken)).Valid);

            await db.Database.ExecuteSqlRawAsync("UPDATE AuditEntries SET DataJson = '{{\"x\":\"tampered\"}}' WHERE Sequence = 2", TestContext.Current.CancellationToken);
            var v = await audit.VerifyAsync(TestContext.Current.CancellationToken);
            Assert.False(v.Valid);
            Assert.Equal(2, v.FirstBrokenSequence);
        });
    }

    [Fact]
    public async Task Audit_ConcurrentAppends_ChainStaysValid()
    {
        await _f.NewClient().GetAsync("/health/ready", TestContext.Current.CancellationToken); // ensure created
        await Parallel.ForAsync(0, 30, TestContext.Current.CancellationToken, async (i, ct) =>
        {
            await using var scope = _f.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent($"test.{i}", null), ct);
        });
        await _f.WithDbAsync(async db =>
        {
            var seqs = await db.AuditEntries.Select(a => a.Sequence).OrderBy(s => s).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);
        });
        await using var s2 = _f.Services.CreateAsyncScope();
        Assert.True((await s2.ServiceProvider.GetRequiredService<IAuditLog>().VerifyAsync(TestContext.Current.CancellationToken)).Valid);
    }

    [Fact]
    public async Task Cookie_IssuedByInstanceA_AcceptedByInstanceB()
    {
        await using var instanceB = new KbAppFactory(_f.DbPath);

        // Sign in on A, capture the cookie, replay it on B (same database = shared Data Protection keys).
        var raw = _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        var signIn = await raw.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "breakglass", password = KbAppFactory.BreakGlassPassword }, TestContext.Current.CancellationToken);
        var setCookie = signIn.Headers.GetValues("Set-Cookie").First(h => h.StartsWith("__Host-kb=", StringComparison.Ordinal)).Split(';')[0];

        var b = instanceB.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        req.Headers.Add("Cookie", setCookie);
        var r = await b.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task SignOut_EndsEverySessionOfThatUser()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = _f.NewClient(); // starts the app so the database exists
        await using (var scope = _f.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Kb.Core.Identity.KbUser>>()
                .CreateAsync(new Kb.Core.Identity.KbUser { UserName = "leaver", DisplayName = "Leaver", CreatedAt = DateTimeOffset.UtcNow }, "Leaver-pass-123!")).Succeeded);
        var raw = _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        async Task<string> SignInAsync() => (await raw.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "leaver", password = "Leaver-pass-123!" }, ct))
            .Headers.GetValues("Set-Cookie").First(h => h.StartsWith("__Host-kb=", StringComparison.Ordinal)).Split(';')[0];
        async Task<HttpStatusCode> SendAsync(HttpMethod m, string path, string cookie)
        {
            using var req = new HttpRequestMessage(m, path);
            req.Headers.Add("Cookie", cookie);
            return (await raw.SendAsync(req, ct)).StatusCode;
        }
        var laptop = await SignInAsync();
        var phone = await SignInAsync();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "/api/v1/me", laptop));

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Post, "/api/v1/auth/signout", laptop));

        // A copied cookie must not outlive sign-out, and (owner's choice, 2026-11-13) signing out anywhere ends every session.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(HttpMethod.Get, "/api/v1/me", laptop));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(HttpMethod.Get, "/api/v1/me", phone));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "/api/v1/me", await SignInAsync()));
    }

    [Fact]
    public async Task Owner_GrantsRole_AndAudits()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var sd = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SpaceDto>(TestContext.Current.CancellationToken);
        var invite = await (await admin.PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = "tech1", displayName = "Tech One" }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        var techId = invite!["id"];
        await admin.PutAsJsonAsync($"/api/v1/spaces/{sd!.Id}/roles", new { principalType = "User", principalId = techId, role = "Contributor" }, TestContext.Current.CancellationToken);

        await _f.WithDbAsync(async db =>
        {
            Assert.True(await db.SpaceRoleAssignments.AnyAsync(a => a.PrincipalId == techId && a.Role == SpaceRole.Contributor));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SpaceRoleGrant));
        });
    }
}
