using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The rules a plugin cannot get around, one test each. Where a rule is already proven elsewhere, the test is named here:
/// - Permission comes before plugins: BusTests.Send_AuthorizesBeforeAnyHook, BusTests.Hook_ReplacingAction_IsAuthorizedAgain.
/// - An enforcing plugin stays locked: LockTests.EnforcingPlugin_FailedLoad_KeepsActionsLocked, LockTests.TokenOfAnAdmin_DoesNotPassALock.
/// - Every admin route refuses a non-admin: RegistryTests.EveryAdminRoute_Is403_ForANonAdmin.
/// - A plugin route is closed to API tokens: BusTests.PluginRoute_Token_403.
/// </summary>
public sealed class PluginRuleTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private const string Plugins = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task InstallAsync(params string[] projects)
    {
        foreach (var project in projects) await TestPlugins.InstallAsync(_admin, TestPlugins.Package(project));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync($"{Plugins}/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private async Task<(Guid Space, Guid Page)> SpaceAsync(string key, string title)
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
        return (space, page);
    }

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name, Guid? space = null, string role = "Contributor")
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, "user-password-0123456789")).Succeeded);
            id = user.Id;
        }
        if (space is { } s) (await _admin.PutAsJsonAsync($"/api/v1/spaces/{s}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return (c, id);
    }

    /// <summary>A plugin sees only what its caller may read: inside a hook, the bus answers for the person who sent the action.</summary>
    [Fact]
    public async Task Hook_ReadingAnotherPage_GetsWhatTheCallerWouldGet()
    {
        await InstallAsync(TestPlugins.Probe);
        var (mine, page) = await SpaceAsync("mine", "My page");
        var (_, secret) = await SpaceAsync("other", "Not for Cara");
        var (cara, _) = await UserAsync("cara", mine);
        await SetAsync("HookReads", $"page.save:{secret}");

        var saved = await cara.PutAsJsonAsync($"/api/v1/pages/{page}", new { title = "My page", contentJson = Doc, baseVersion = "1.0" }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("refused 404", (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/plugins/kb.test.probe/counters", Ct)).GetProperty("lastHookRead").GetString());

        // The same hook, for a caller who may read that page, gets it.
        (await _admin.PutAsJsonAsync($"/api/v1/pages/{page}", new { title = "My page", contentJson = Doc, baseVersion = "1.1" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("Not for Cara", (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/plugins/kb.test.probe/counters", Ct)).GetProperty("lastHookRead").GetString());
    }

    /// <summary>The audit log cannot be silenced: a hook can add an entry, and the core's entry is written all the same.</summary>
    [Fact]
    public async Task Hook_AddingAnAuditEntry_KeepsTheCoresEntry()
    {
        await InstallAsync(TestPlugins.Probe);
        var (_, page) = await SpaceAsync("mine", "My page");
        await SetAsync("HookAudit", "page.delete");

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);

        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action == "page.delete" || a.Action == "probe.hook").OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync(Ct);
            Assert.Equal(["probe.hook", "page.delete"], entries);
        });
    }

    /// <summary>What a hook holds gives it no way to take an entry back: the context can add to the audit trail and nothing else.</summary>
    [Fact]
    public void ActionContext_HasNoWayToRemoveAnAuditEntry()
    {
        var members = typeof(ActionContext<ReadPage>).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.DeclaringType != typeof(object) && m is not ConstructorInfo && !(m is MethodInfo { IsSpecialName: true }))
            .Select(m => m.Name).Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["Action", "Actor", "Audit", "Cancel", "Name", "Services"], members);
        Assert.Empty(typeof(ActionContext<ReadPage>).GetConstructors(BindingFlags.Public | BindingFlags.Instance)); // only the bus makes one
    }

    /// <summary>Plugin to plugin runs as the caller: an action of one plugin, sent from a route of another, runs as the signed-in person.</summary>
    [Fact]
    public async Task LibraryAction_SentByADependentsRoute_RunsAsTheSignedInUser()
    {
        await InstallAsync(TestPlugins.Lib, TestPlugins.Dependent);
        var (cara, id) = await UserAsync("cara");

        var greeting = await cara.GetFromJsonAsync<JsonElement>("/api/v1/plugins/kb.test.dependent/greet/world", Ct);

        Assert.Equal(id, greeting.GetProperty("by").GetGuid());
        Assert.Equal("Hello, WORLD.", greeting.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("/api/v1/plugins/kb.test.dependent/greet/world", Ct)).StatusCode); // not signed in: the route is not reached
    }

    /// <summary>Recovery always works: with a plugin that breaks the start, Safe Mode starts the app and people sign in.</summary>
    [Fact]
    public async Task SafeMode_LocalSignIn_Works_WithEveryPluginBroken()
    {
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Enforcing = true }));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        var (_, cara) = await UserAsync("cara");
        await SetAsync("FailOn", "configure");
        await _f.DisposeAsync();
        _f = new KbAppFactory(_f.DbPath, settings: new Dictionary<string, string> { ["Kb:Plugins:SafeMode"] = "true" }, workFolder: _f.WorkFolder);

        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync("/health/ready", Ct)).StatusCode);
        var admin = await _f.SignedInBreakGlassAsync();
        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "cara", password = "user-password-0123456789" }, Ct)).StatusCode);
        Assert.Equal(cara, (await c.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid());
        // The Admin can reach the Plugins screen's data and remove the plugin.
        Assert.True((await admin.GetFromJsonAsync<JsonElement>(Plugins, Ct)).GetProperty("safeMode").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Plugins}/kb.test.probe", Ct)).StatusCode);
    }
}
