using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>A plugin's script for the browser: which scripts the app tells the browser to load, which files of a package it serves, and what it does with a script's error.</summary>
public sealed class PluginUiTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe, m => m with { UiApiVersion = "1.0.0", Ui = new PluginUi { Script = "ui/index.js" } }));
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Lib)); // a plugin with no script
        await RestartAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    private async Task<HttpClient> UserAsync(string name)
    {
        await using (var scope = _f.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<string> ScriptAsync(HttpClient c)
    {
        var list = await c.GetFromJsonAsync<JsonElement>("/api/v1/ui/plugins", Ct);
        return list.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.test.probe").GetProperty("script").GetString()!;
    }

    [Fact]
    public async Task UiPlugins_ListsLoadedPluginsThatHaveAScript_ForAnyoneSignedIn()
    {
        var gail = await UserAsync("gail");
        var list = await gail.GetFromJsonAsync<JsonElement>("/api/v1/ui/plugins", Ct);

        Assert.Equal(KbHost.UiApiVersion, list.GetProperty("uiApiVersion").GetString());
        var probe = Assert.Single(list.GetProperty("plugins").EnumerateArray()); // the library has no script
        Assert.Equal(("kb.test.probe", "1.0.0"), (probe.GetProperty("id").GetString(), probe.GetProperty("version").GetString()));
        Assert.StartsWith("/plugins/kb.test.probe/1.0.0/ui/index.js?v=", probe.GetProperty("script").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("/api/v1/ui/plugins", Ct)).StatusCode);
    }

    [Fact]
    public async Task PluginFiles_ServesTheScript_AndOnlyTheUiFolder()
    {
        var gail = await UserAsync("gail");
        var script = await gail.GetAsync(await ScriptAsync(gail), Ct);

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", script.Headers.CacheControl?.ToString());
        Assert.Contains("export default function", await script.Content.ReadAsStringAsync(Ct));

        // Nothing else of the package can be fetched: not the manifest, not the assembly, not a type the app does not serve, not by climbing out.
        string[] refused =
        [
            "/plugins/kb.test.probe/1.0.0/kb-plugin.json", "/plugins/kb.test.probe/1.0.0/Kb.TestPlugin.Probe.dll", "/plugins/kb.test.probe/1.0.0/ui/notes.txt",
            "/plugins/kb.test.probe/1.0.0/ui/%2e%2e/kb-plugin.json", "/plugins/kb.test.probe/1.0.0/ui/..%5ckb-plugin.json", "/plugins/kb.test.probe/1.0.0/ui/nothing.js",
            "/plugins/kb.test.probe/1.0.0/ui/", "/plugins/kb.test.probe/1.0.0/.sha256",
        ];
        foreach (var path in refused) Assert.Equal((path, HttpStatusCode.NotFound), (path, (await gail.GetAsync(path, Ct)).StatusCode));
    }

    /// <summary>
    /// Only an address that carries the package's hash may be kept for good. A file a script fetches by a relative address
    /// (a stylesheet, an imported module) has no hash in its address: the browser must ask again, or it would keep the old file
    /// after the same version is installed again with other content.
    /// </summary>
    [Fact]
    public async Task PluginFiles_AreKeptForGood_OnlyUnderAnAddressWithThePackagesHash()
    {
        var script = await ScriptAsync(_admin);

        Assert.Contains("immutable", (await _admin.GetAsync(script, Ct)).Headers.CacheControl?.ToString());
        foreach (var address in new[] { script.Split('?')[0], script.Split('?')[0] + "?v=000000000000" })
        {
            var answer = await _admin.GetAsync(address, Ct);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal("no-cache, private", answer.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task PluginFiles_NeedSignIn_TheRightVersion_AndALoadedPlugin()
    {
        var script = await ScriptAsync(_admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync(script, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/plugins/kb.test.probe/9.9.9/ui/index.js", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/plugins/kb.nothing/1.0.0/ui/index.js", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"{Url}/kb.test.probe/state", new { enabled = false }, Ct)).StatusCode);
        await RestartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(script, Ct)).StatusCode);
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ui/plugins", Ct)).GetProperty("plugins").EnumerateArray());
    }

    /// <summary>
    /// Any signed-in person's browser can report a script error. So a report is never counted as a failure of the plugin
    /// (that would let a user switch a plugin off), and one person can send only a few a minute.
    /// </summary>
    [Fact]
    public async Task UiError_IsAccepted_NotCountedAsFailure_AndLimitedPerPerson()
    {
        var gail = await UserAsync("gail");
        Task<HttpResponseMessage> ReportAsync(HttpClient c) => c.PostAsJsonAsync("/api/v1/ui/errors", new { plugin = "kb.test.probe", where = "page.panel", message = "It threw.\nSecond line." }, Ct);

        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(gail)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await ReportAsync(gail)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(_admin)).StatusCode); // someone else is not held back by her reports

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/v1/ui/errors", new { plugin = "kb.nothing", where = "x", message = "y" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReportAsync(_f.NewClient())).StatusCode);
        await _f.WithDbAsync(async db => Assert.Equal(PluginState.Enabled, (await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct)).State));
    }
}
