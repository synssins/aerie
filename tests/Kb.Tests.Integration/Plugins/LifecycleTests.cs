using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>Install, update, enable, disable and remove through /api/v1/admin/plugins. The app asks for a restart; the test does the restart.</summary>
public sealed class LifecycleTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, byte[] package) => c.PostAsync(Url, new ByteArrayContent(package), Ct);

    private static async Task<string?> DetailAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("detail", out var d) ? d.GetString() : null;

    private static async Task RefusedAsync(Task<HttpResponseMessage> call, HttpStatusCode status, string detail)
    {
        var r = await call;
        Assert.Equal(status, r.StatusCode);
        Assert.Equal(detail, await DetailAsync(r));
    }

    private async Task<HttpClient> RestartAsync()
    {
        _f = await _f.RestartAsync();
        return await _f.SignedInBreakGlassAsync();
    }

    private static Task<HttpResponseMessage> StateAsync(HttpClient c, string id, bool enabled) => c.PutAsJsonAsync($"{Url}/{id}/state", new { enabled }, Ct);

    private static async Task<JsonElement> PluginAsync(HttpClient c, string id) =>
        (await c.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);

    private bool SinkOf(string id) => _f.Services.GetServices<IAuditSink>().Any(s => s.ProviderId == id);

    [Fact]
    public async Task Upload_ValidPackage_StoresAndAsksForRestart()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var package = TestPlugins.Package(TestPlugins.Probe);
        var r = await UploadAsync(admin, package);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("kb.test.probe", "1.0.0", true), (body.GetProperty("id").GetString(), body.GetProperty("version").GetString(), body.GetProperty("restart").GetBoolean()));
        Assert.Single(_f.Restarter.Reasons);
        Assert.False(SinkOf("kb.test.probe")); // not before the restart

        await _f.WithDbAsync(async db =>
        {
            var entry = JsonSerializer.Deserialize<Dictionary<string, string>>((await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.PluginInstall, Ct)).DataJson)!;
            Assert.Equal((PluginPackageReader.Read(package).Value!.Sha256, "upload", "1.0.0"), (entry["sha256"], entry["source"], entry["version"]));
            Assert.Equal("1", (await db.RuntimeSettings.SingleAsync(s => s.Key == "Plugins.Generation", Ct)).Value);
        });
    }

    [Fact]
    public async Task Upload_ThenRestart_PluginIsLoaded()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        var admin = await RestartAsync();
        Assert.True(SinkOf("kb.test.probe"));
        var p = await PluginAsync(admin, "kb.test.probe");
        Assert.Equal(("Test probe", "Enabled", true, "Uploaded", "upload"),
            (p.GetProperty("name").GetString(), p.GetProperty("state").GetString(), p.GetProperty("loaded").GetBoolean(), p.GetProperty("origin").GetString(), p.GetProperty("source").GetString()));
        Assert.Contains(p.GetProperty("settings").EnumerateArray(), s => s.GetProperty("key").GetString() == "Token" && s.GetProperty("secret").GetBoolean());
        Assert.Empty(_f.Restarter.Reasons); // nothing changed since this start
    }

    [Fact]
    public async Task Upload_NotAdmin_403_AndGarbage_400()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<Kb.Core.Identity.KbUser>>().CreateAsync(new() { UserName = "tech", DisplayName = "Tech", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        var user = _f.NewClient();
        (await user.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "tech", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(user, TestPlugins.Package(TestPlugins.Probe))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StateAsync(user, "kb.email.smtp", false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.DeleteAsync($"{Url}/kb.email.smtp", Ct)).StatusCode);

        var admin = await _f.SignedInBreakGlassAsync();
        await RefusedAsync(UploadAsync(admin, [1, 2, 3, 4]), HttpStatusCode.BadRequest, "Not a plugin package.");
        Assert.Empty(_f.Restarter.Reasons);
    }

    [Fact]
    public async Task Upload_SameVersionTwice_OlderVersion_OrABuiltInId_409()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Version = "2.0.0" }));
        await RefusedAsync(UploadAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Version = "2.0.0" })), HttpStatusCode.Conflict, "Version 2.0.0 is already installed.");
        await RefusedAsync(UploadAsync(admin, TestPlugins.Package(TestPlugins.Probe)), HttpStatusCode.Conflict, "A newer version is installed.");
        await RefusedAsync(UploadAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Id = "kb.email.smtp" })), HttpStatusCode.Conflict, "Built-in plugins cannot be replaced.");
        await RefusedAsync(UploadAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Id = "kb.test-probe" })), HttpStatusCode.Conflict, "Conflicts with kb.test.probe.");
        await RefusedAsync(UploadAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Id = "kb.other", AbstractionsVersion = "1.0.0" })), HttpStatusCode.Conflict, "Built for a newer plugin API (1.0.0).");
    }

    [Fact]
    public async Task Upload_MissingRequirement_409_NamesIt()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await RefusedAsync(UploadAsync(admin, TestPlugins.Package(TestPlugins.Dependent)), HttpStatusCode.Conflict, "Requires kb.test.lib 1.0.0 or later.");
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Lib));
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Dependent));
    }

    [Fact]
    public async Task Update_ReplacesTheOldPackage()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        admin = await RestartAsync();
        await StateAsync(admin, "kb.test.probe", false); // an update keeps the state
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Version = "2.0.0" }));
        admin = await RestartAsync();

        var p = await PluginAsync(admin, "kb.test.probe");
        Assert.Equal(("2.0.0", "Disabled"), (p.GetProperty("version").GetString(), p.GetProperty("state").GetString()));
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(["2.0.0"], await db.PluginPackages.Select(x => x.Version).ToListAsync(Ct));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.PluginUpdate, Ct));
        });
    }

    [Fact]
    public async Task GoingBack_RemoveThenUploadTheOlderPackage_Works()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Version = "2.0.0" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Url}/kb.test.probe", Ct)).StatusCode);
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        admin = await RestartAsync();
        Assert.True(SinkOf("kb.test.probe"));
        Assert.Equal("1.0.0", (await PluginAsync(admin, "kb.test.probe")).GetProperty("version").GetString());
    }

    [Fact]
    public async Task DisableOrRemove_WithEnabledDependent_409_AndEnable_WithDisabledRequirement_409()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Lib));
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Dependent));
        admin = await RestartAsync();
        Assert.Equal(["kb.test.dependent"], (await PluginAsync(admin, "kb.test.lib")).GetProperty("requiredBy").EnumerateArray().Select(x => x.GetString()));

        await RefusedAsync(StateAsync(admin, "kb.test.lib", false), HttpStatusCode.Conflict, "Required by Test dependent.");
        await RefusedAsync(admin.DeleteAsync($"{Url}/kb.test.lib", Ct), HttpStatusCode.Conflict, "Required by Test dependent.");

        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.dependent", false)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.lib", false)).StatusCode);
        await RefusedAsync(StateAsync(admin, "kb.test.dependent", true), HttpStatusCode.Conflict, "Requires Test library.");
        Assert.Equal(HttpStatusCode.NotFound, (await StateAsync(admin, "kb.nothing", true)).StatusCode);
    }

    [Fact]
    public async Task Disable_ThenRestart_NotLoaded_SettingsKept_ThenEnableLoadsItAgain()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Greeting", Value = "Hello", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        admin = await RestartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.probe", false)).StatusCode);
        Assert.Single(_f.Restarter.Reasons);
        admin = await RestartAsync();

        Assert.False(SinkOf("kb.test.probe"));
        Assert.Equal(("Disabled", false), ((await PluginAsync(admin, "kb.test.probe")).GetProperty("state").GetString(), (await PluginAsync(admin, "kb.test.probe")).GetProperty("loaded").GetBoolean()));
        await _f.WithDbAsync(async db => Assert.Equal("Hello", (await db.PluginSettingValues.SingleAsync(Ct)).Value));

        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.probe", true)).StatusCode);
        await RestartAsync();
        Assert.True(SinkOf("kb.test.probe"));
    }

    [Fact]
    public async Task Enable_AfterTheCoreDisabledIt_ClearsTheReason()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "FailOn", Value = "configure", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        admin = await RestartAsync();
        var p = await PluginAsync(admin, "kb.test.probe");
        Assert.Equal(("DisabledByCore", "Probe failed in Configure."), (p.GetProperty("state").GetString(), p.GetProperty("reason").GetString()));

        await _f.WithDbAsync(async db => { db.PluginSettingValues.RemoveRange(db.PluginSettingValues); await db.SaveChangesAsync(Ct); });
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.probe", true)).StatusCode);
        admin = await RestartAsync();
        p = await PluginAsync(admin, "kb.test.probe");
        Assert.Equal(("Enabled", JsonValueKind.Null, true), (p.GetProperty("state").GetString(), p.GetProperty("reason").ValueKind, p.GetProperty("loaded").GetBoolean()));
    }

    [Fact]
    public async Task Remove_BuiltIn_409_AndRemove_DeletesPackagesAndSettings()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await RefusedAsync(admin.DeleteAsync($"{Url}/kb.email.smtp", Ct), HttpStatusCode.Conflict, "Built-in plugins cannot be removed.");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Url}/kb.nothing", Ct)).StatusCode);

        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Greeting", Value = "Hello", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Url}/kb.test.probe?deleteData=false", Ct)).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.PluginPackages.AnyAsync(Ct));
            Assert.False(await db.PluginSettingValues.AnyAsync(Ct));
            Assert.False(await db.PluginStatuses.AnyAsync(s => s.PluginId == "kb.test.probe", Ct));
        });
        admin = await RestartAsync();
        Assert.False(SinkOf("kb.test.probe"));
        Assert.DoesNotContain("kb.test.probe", await admin.GetStringAsync(Url, Ct));
    }

    [Fact]
    public async Task BuiltIn_CanBeDisabledAndEnabled()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.email.smtp", false)).StatusCode);
        admin = await RestartAsync();
        var p = await PluginAsync(admin, "kb.email.smtp");
        Assert.Equal(("Disabled", false, "BuiltIn"), (p.GetProperty("state").GetString(), p.GetProperty("loaded").GetBoolean(), p.GetProperty("origin").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.email.smtp", true)).StatusCode);
    }

    [Fact]
    public async Task DisableOrRemove_WhileItsStorageOrLogKindIsInUse_409()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Register", Value = "kinds", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        admin = await RestartAsync();

        var target = await (await admin.PostAsJsonAsync("/api/v1/admin/logs/targets", new { name = "Probe log", kind = "probe-log", settings = new { }, enabled = true }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        await RefusedAsync(StateAsync(admin, "kb.test.probe", false), HttpStatusCode.Conflict, "In use for log shipping.");
        await RefusedAsync(admin.DeleteAsync($"{Url}/kb.test.probe", Ct), HttpStatusCode.Conflict, "In use for log shipping.");
        await admin.DeleteAsync($"/api/v1/admin/logs/targets/{target.GetProperty("id").GetString()}", Ct);

        var place = await (await admin.PostAsJsonAsync("/api/v1/admin/storage/places", new { name = "Probe store", kind = "probe-store", settings = new { } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.probe", false)).StatusCode); // a place that is only remembered does not hold it
        Assert.Equal(HttpStatusCode.NoContent, (await StateAsync(admin, "kb.test.probe", true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/admin/storage/move", new { toId = place.GetProperty("id").GetString() }, Ct)).StatusCode);
        await RefusedAsync(StateAsync(admin, "kb.test.probe", false), HttpStatusCode.Conflict, "In use for file storage.");
        await RefusedAsync(admin.DeleteAsync($"{Url}/kb.test.probe", Ct), HttpStatusCode.Conflict, "In use for file storage.");
    }

    [Fact]
    public async Task SecondInstance_SeesTheChange_AsksForRestart()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await using var second = _f.SecondInstance();
        await second.SignedInBreakGlassAsync();

        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Probe));
        for (var waited = 0; second.Restarter.Reasons.Count == 0 && waited < 6000; waited += 100) await Task.Delay(100, Ct);

        Assert.Single(second.Restarter.Reasons);
        Assert.Single(_f.Restarter.Reasons); // the server that made the change asked once, not again when it saw its own change
    }

    [Fact]
    public async Task EveryChange_IsAudited_WithHash()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var package = TestPlugins.Package(TestPlugins.Probe, m => m with { Enforcing = true });
        var sha = PluginPackageReader.Read(package).Value!.Sha256;
        await TestPlugins.InstallAsync(admin, package);
        await StateAsync(admin, "kb.test.probe", false);
        await StateAsync(admin, "kb.test.probe", true);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Url}/kb.test.probe/release-lock", null, Ct)).StatusCode);
        await admin.DeleteAsync($"{Url}/kb.test.probe", Ct);

        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action.StartsWith("plugin.")).OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal(["plugin.install", "plugin.disable", "plugin.enable", "plugin.lock.release", "plugin.remove"], entries.Select(e => e.Action));
            Assert.All(entries, e =>
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(e.DataJson)!;
                Assert.Equal(("kb.test.probe", sha, "upload"), (e.TargetId, data["sha256"], data["source"]));
            });
            Assert.Equal("5", (await db.RuntimeSettings.SingleAsync(s => s.Key == "Plugins.Generation", Ct)).Value);
        });
        Assert.Equal(5, _f.Restarter.Reasons.Count);
    }
}
