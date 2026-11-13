using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Web;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The page routes go through the action bus: each answers with its action's name, and a plugin can refuse, change, shape and follow them.</summary>
public sealed class PagePipelineTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private Task<HttpResponseMessage> CreateAsync(string title) => _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title }, Ct);
    private async Task<PageDto> CreatedAsync(string title) => (await (await CreateAsync(title)).Content.ReadFromJsonAsync<PageDto>(Ct))!;
    private Task<HttpResponseMessage> SaveAsync(Guid id, string title, string baseVersion = "1.0") =>
        _admin.PutAsJsonAsync($"/api/v1/pages/{id}", new { title, contentJson = Doc, baseVersion }, Ct);
    private async Task<PageDto> ReadAsync(Guid id) => (await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{id}", Ct))!;
    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    [Fact]
    public async Task EveryPageRoute_AnswersWithItsActionName()
    {
        var created = await CreateAsync("Reset password");
        Assert.Equal((HttpStatusCode.Created, "page.create"), (created.StatusCode, Action(created)));
        var id = (await created.Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
        Assert.Equal($"/api/v1/pages/{id}", created.Headers.Location?.ToString());

        Assert.Equal("tree.read", Action(await _admin.GetAsync($"/api/v1/spaces/{_space}/tree", Ct)));
        Assert.Equal("page.read", Action(await _admin.GetAsync($"/api/v1/pages/{id}", Ct)));
        Assert.Equal("page.save", Action(await SaveAsync(id, "Reset a password")));
        // A refusal by the core names the action too.
        var missing = await _admin.GetAsync($"/api/v1/pages/{Guid.NewGuid()}", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "page.read"), (missing.StatusCode, Action(missing)));
    }

    [Fact]
    public async Task Save_RefusedByHook_Is422_PageUnchanged()
    {
        var page = await CreatedAsync("Reset password");
        await SetAsync("Refuse", "page.save");

        var r = await SaveAsync(page.Id, "Changed");

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        var after = await ReadAsync(page.Id);
        Assert.Equal(("Reset password", "1.0"), (after.Title, after.Version));
    }

    [Fact]
    public async Task Save_HookChangesTheTitle_TheChangedTitleIsSaved()
    {
        var page = await CreatedAsync("Reset password");
        await SetAsync("Rewrite", "page.save:Title=Changed by the probe");

        var saved = await (await SaveAsync(page.Id, "Typed by the person")).Content.ReadFromJsonAsync<PageDto>(Ct);

        Assert.Equal(("Changed by the probe", "1.1"), (saved!.Title, saved.Version));
        Assert.Equal("Changed by the probe", (await ReadAsync(page.Id)).Title);
    }

    [Fact]
    public async Task Read_FilterTrimsTheResult()
    {
        var page = await CreatedAsync("Reset password");
        await SetAsync("Blank", "page.read:Title");

        Assert.Equal("", (await ReadAsync(page.Id)).Title);
        // The filter shapes the answer, not the page: the tree, another read, still has the title.
        var tree = await _admin.GetFromJsonAsync<List<TreeNodeDto>>($"/api/v1/spaces/{_space}/tree", Ct);
        Assert.Equal("Reset password", Assert.Single(tree!).Title);
    }

    [Fact]
    public async Task Read_FilterThrows_Is422_NothingIsAnswered()
    {
        var page = await CreatedAsync("Reset password");
        await SetAsync("FailFilter", "page.read");

        var r = await _admin.GetAsync($"/api/v1/pages/{page.Id}", Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.DoesNotContain("Reset password", await r.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Create_AfterHandler_ReceivesTheCreatedPage()
    {
        var page = await CreatedAsync("Reset password");

        var counters = await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct);
        for (var waited = 0; counters.GetProperty("lastAfterAny").GetString() != $"page.create|{page.Id}" && waited < 5000; waited += 50)
        {
            await Task.Delay(50, Ct);
            counters = await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct);
        }
        Assert.Equal($"page.create|{page.Id}", counters.GetProperty("lastAfterAny").GetString());
        Assert.Equal(1, counters.GetProperty("byAction").GetProperty("after:page.create").GetInt32());
    }

    [Fact]
    public async Task Route_ThatNamesOneActionAndSendsAnother_Fails()
    {
        var r = await _admin.GetAsync($"{Probe}/mismatch", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.False((await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct)).GetProperty("written").TryGetProperty("mismatch", out _)); // the action did not run
    }
}
