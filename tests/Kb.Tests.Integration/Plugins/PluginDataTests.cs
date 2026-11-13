using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration.Plugins;

/// <summary>A plugin's own tables: prefixed, made at start, kept when the plugin is removed unless the Admin deletes its data.</summary>
public sealed class PluginDataTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    /// <summary>Installs the probe under an id, with what it registers, and restarts so it is loaded.</summary>
    private async Task InstallAsync(string id = "kb.test.probe", string register = "data")
    {
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Id = id }));
        await RestartAsync(); // its settings can be set once it is known to the running app
        (await _admin.PutAsJsonAsync($"{Url}/{id}/settings", new { values = new Dictionary<string, string> { ["Register"] = register } }, Ct)).EnsureSuccessStatusCode();
        await RestartAsync();
    }

    private async Task<List<string>> TablesAsync()
    {
        var tables = new List<string>();
        await _f.WithDbAsync(async db => tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'plg%'").OrderBy(n => n).ToListAsync(Ct));
        return tables;
    }

    private async Task<JsonElement?> ListedAsync(string id) =>
        (await _admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("plugins").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(p => p!.Value.GetProperty("id").GetString() == id);

    [Fact]
    public async Task PluginTables_ArePrefixed_AndRowsAreKept()
    {
        await InstallAsync();
        Assert.Equal(["plg_kb_test_probe__notes"], await TablesAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync("/api/v1/plugins/kb.test.probe/notes", new { text = "first" }, Ct)).StatusCode);
        await RestartAsync(); // the next start finds the table and leaves it alone
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync("/api/v1/plugins/kb.test.probe/notes", new { text = "second" }, Ct)).StatusCode);
        Assert.Equal(["first", "second"], await _admin.GetFromJsonAsync<List<string>>("/api/v1/plugins/kb.test.probe/notes", Ct));
    }

    [Fact]
    public async Task PluginWithoutData_HasNoTables()
    {
        await InstallAsync(register: "");
        Assert.Empty(await TablesAsync());
    }

    [Fact]
    public async Task FailingTableSetup_DisablesThePlugin_AppStarts()
    {
        await InstallAsync(register: "baddata");

        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync("/health/ready", Ct)).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            var status = await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct);
            Assert.Equal((PluginState.DisabledByCore, "Probe tables failed."), (status.State, status.Reason));
        });
        Assert.Empty(await TablesAsync());
    }

    [Fact]
    public async Task Remove_KeepsTablesByDefault_ListsItAsRemoved()
    {
        await InstallAsync();
        await _admin.PostAsJsonAsync("/api/v1/plugins/kb.test.probe/notes", new { text = "kept" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Url}/kb.test.probe", Ct)).StatusCode);
        await RestartAsync();

        Assert.Equal(["plg_kb_test_probe__notes"], await TablesAsync());
        var listed = await ListedAsync("kb.test.probe");
        Assert.NotNull(listed);
        Assert.True(listed.Value.GetProperty("removed").GetBoolean());
        Assert.False(listed.Value.GetProperty("loaded").GetBoolean());

        // Installing it again finds its rows.
        await InstallAsync();
        Assert.Equal(["kept"], await _admin.GetFromJsonAsync<List<string>>("/api/v1/plugins/kb.test.probe/notes", Ct));
        Assert.False((await ListedAsync("kb.test.probe"))!.Value.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public async Task Remove_WithDeleteData_DropsOnlyThatPluginsTables()
    {
        await InstallAsync();
        await InstallAsync("kb.test.probe2");
        Assert.Equal(["plg_kb_test_probe2__notes", "plg_kb_test_probe__notes"], await TablesAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Url}/kb.test.probe?deleteData=true", Ct)).StatusCode);
        await RestartAsync();

        Assert.Equal(["plg_kb_test_probe2__notes"], await TablesAsync());
        Assert.Null(await ListedAsync("kb.test.probe"));
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsJsonAsync("/api/v1/plugins/kb.test.probe2/notes", new { text = "still works" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task DeleteData_Later_ForARemovedPlugin()
    {
        await InstallAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.DeleteAsync($"{Url}/kb.test.probe/data", Ct)).StatusCode); // not while it is installed
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Url}/kb.test.probe", Ct)).StatusCode);
        await RestartAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Url}/kb.test.probe/data", Ct)).StatusCode);

        Assert.Empty(await TablesAsync());
        Assert.Null(await ListedAsync("kb.test.probe"));
        await _f.WithDbAsync(async db => Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == "plugin.data.delete", Ct)));
    }
}
