using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The lock of an enforcing plugin: when the core had to disable it, the actions it guarded stay refused for everyone but an Admin
/// until an Admin releases the lock. Sign-in is never locked.
/// </summary>
public sealed class LockTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private const string Plugins = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space, _page, _user;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe, m => m with { Enforcing = true }));
        await RestartAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        _page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Reset password" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
        await using var scope = _f.Services.CreateAsyncScope();
        var user = new KbUser { UserName = "cara", DisplayName = "Cara", CreatedAt = DateTimeOffset.UtcNow };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, "user-password-0123456789")).Succeeded);
        _user = user.Id;
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = _user.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync(KbAppFactory? next = null)
    {
        _f = next ?? await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    /// <summary>The probe fails at the next start, so the core disables it. It is enforcing, so that locks what it guarded.</summary>
    private async Task FailTheProbeAsync()
    {
        (await _admin.PutAsJsonAsync($"{Plugins}/kb.test.probe/settings", new { values = new Dictionary<string, string> { ["FailOn"] = "configure" } }, Ct)).EnsureSuccessStatusCode();
        await RestartAsync();
        await _f.WithDbAsync(async db =>
        {
            var status = await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct);
            Assert.Equal(PluginState.DisabledByCore, status.State);
            Assert.NotNull(status.LockedAt);
        });
    }

    /// <summary>Signs the Contributor in. This works while the lock holds: sign-in is never locked.</summary>
    private async Task<HttpClient> ContributorAsync()
    {
        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "cara", password = "user-password-0123456789" }, Ct)).StatusCode);
        return c;
    }

    private async Task<HttpResponseMessage> SaveAsync(HttpClient c)
    {
        var version = (await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{_page}", Ct))!.Version;
        return await c.PutAsJsonAsync($"/api/v1/pages/{_page}", new { title = "Reset password", contentJson = Doc, baseVersion = version }, Ct);
    }

    [Fact]
    public async Task Start_RecordsWhatThePluginGuards()
    {
        await _f.WithDbAsync(async db =>
        {
            var guards = JsonSerializer.Deserialize<JsonElement>((await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct)).GuardsJson);
            var actions = guards.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("page.save", actions); // a before hook
            Assert.Contains("page.read", actions); // a filter, and a before hook
            Assert.Contains("probe.echo", actions);
            Assert.Equal(actions.Order(StringComparer.Ordinal), actions);
        });
    }

    [Fact]
    public async Task EnforcingPlugin_FailedLoad_KeepsActionsLocked()
    {
        await FailTheProbeAsync();
        var cara = await ContributorAsync();

        var refused = await SaveAsync(cara);
        Assert.Equal((HttpStatusCode)423, refused.StatusCode);
        Assert.Equal("Test probe is disabled.", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Equal((HttpStatusCode)423, (await cara.GetAsync($"/api/v1/pages/{_page}", Ct)).StatusCode); // a read it filtered is locked too
        // An Admin is not locked out: someone has to be able to look and to repair.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(_admin)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"{Plugins}/kb.test.probe/release-lock", null, Ct)).StatusCode);
        await RestartAsync();
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(await ContributorAsync())).StatusCode);
    }

    [Fact]
    public async Task Lock_IsCheckedAfterTheCoresOwnPermissionCheck()
    {
        await FailTheProbeAsync();
        // Someone with no role in the space learns nothing from the lock: the page still does not exist for them.
        await using (var scope = _f.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(new KbUser { UserName = "dan", DisplayName = "Dan", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        var dan = _f.NewClient();
        (await dan.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "dan", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await dan.GetAsync($"/api/v1/pages/{_page}", Ct)).StatusCode);
    }

    [Fact]
    public async Task TokenOfAnAdmin_DoesNotPassALock()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/tokens", new { name = "Agent", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)_space, access = "Write" } }, days = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
        await FailTheProbeAsync();
        var token = _f.NewClient();
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", text);

        Assert.Equal((HttpStatusCode)423, (await SaveAsync(token)).StatusCode);
    }

    [Fact]
    public async Task PluginDisabledByAdmin_LocksNothing()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"{Plugins}/kb.test.probe/state", new { enabled = false }, Ct)).StatusCode);
        await RestartAsync();

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(await ContributorAsync())).StatusCode);
    }

    [Fact]
    public async Task SafeMode_IgnoresLocks()
    {
        await FailTheProbeAsync();
        await _f.DisposeAsync();
        await RestartAsync(new KbAppFactory(_f.DbPath, settings: new Dictionary<string, string> { ["Kb:Plugins:SafeMode"] = "true" }, workFolder: _f.WorkFolder));

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(await ContributorAsync())).StatusCode);
    }
}
