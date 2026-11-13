using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration.Plugins;

/// <summary>
/// An editor node the server does not know is kept as sent and read as a placeholder; a node a plugin renders is read through
/// the plugin by search, history and exports.
/// </summary>
public sealed class EditorNodeTests : IAsyncLifetime
{
    private const string Plugins = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    // Odd spacing and key order on purpose: the server must not rewrite what the editor sent.
    private static string Doc(string label) =>
        "{\"content\":[ {\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Before\"}]},\n" +
        "  {\"attrs\":{\"label\":\"" + label + "\",\"extra\":{\"n\":1}},\"type\":\"probeBox\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"insidetheword\"}]}]} ],\"type\":\"doc\"}";

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    /// <summary>Installs the probe (under another id too, when given) with a value of its Register setting.</summary>
    private async Task InstallAsync(string register, string id = "kb.test.probe", string? failOn = null)
    {
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Id = id }));
        await RestartAsync();
        var values = new Dictionary<string, string> { ["Register"] = register };
        if (failOn is not null) values["FailOn"] = failOn;
        (await _admin.PutAsJsonAsync($"{Plugins}/{id}/settings", new { values }, Ct)).EnsureSuccessStatusCode();
        await RestartAsync();
    }

    private async Task<PageDto> PageAsync(string content)
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "docs", name = "Docs" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        var r = await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Boxes", contentJson = content }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;
    }

    private async Task<string> MarkdownAsync(Guid page) => (await _admin.GetFromJsonAsync<PageMarkdownDto>($"/api/v1/pages/{page}/markdown", Ct))!.Markdown;

    private async Task<int> FoundAsync(string word) => (await _admin.GetFromJsonAsync<SearchResponse>($"/api/v1/search?q={word}", Ct))!.Total;

    private async Task<(PluginState State, string? Reason)> StatusAsync(string id)
    {
        (PluginState, string?) status = default;
        await _f.WithDbAsync(async db =>
        {
            var s = await db.PluginStatuses.SingleAsync(x => x.PluginId == id, Ct);
            status = (s.State, s.Reason);
        });
        return status;
    }

    [Fact]
    public async Task UnknownNode_IsStoredAsSent_FoundBySearch_AndShownAsAPlaceholder()
    {
        var page = await PageAsync(Doc("Alpha"));

        Assert.Equal(Doc("Alpha"), page.ContentJson);
        Assert.Equal(1, await FoundAsync("insidetheword"));
        Assert.Equal("Before\n\n[probeBox] insidetheword", await MarkdownAsync(page.Id));

        // A save by an editor that does not know the node sends it back as it got it; it is still stored as sent.
        var saved = await _admin.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { title = "Boxes", contentJson = Doc("Beta"), baseVersion = "1.0" }, Ct);
        Assert.Equal(Doc("Beta"), (await saved.Content.ReadFromJsonAsync<PageDto>(Ct))!.ContentJson);
        Assert.Equal(Doc("Beta"), (await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page.Id}", Ct))!.ContentJson);
    }

    [Fact]
    public async Task EditorNode_Renderer_IsUsedBySearchDiffAndExport()
    {
        await InstallAsync("nodes");
        var page = await PageAsync(Doc("Alpha"));

        // Search finds the page by a word only the plugin's renderer gives; the text inside the node is the renderer's to give or not.
        Assert.Equal(1, await FoundAsync("probewordalpha"));
        Assert.Equal(0, await FoundAsync("insidetheword"));
        Assert.Equal("Before\n\n> [box] Alpha", await MarkdownAsync(page.Id));

        (await _admin.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { title = "Boxes", contentJson = Doc("Beta"), baseVersion = "1.0" }, Ct)).EnsureSuccessStatusCode();
        var diff = (await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page.Id}/diff?from=1.0&to=1.1", Ct))
            .GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("op").GetString()} {l.GetProperty("text").GetString()}").ToList();
        Assert.Contains("Del > [box] Alpha", diff);
        Assert.Contains("Add > [box] Beta", diff);

        var docx = await (await _admin.GetAsync($"/api/v1/pages/{page.Id}/export?format=docx", Ct)).Content.ReadAsByteArrayAsync(Ct);
        using var zip = new ZipArchive(new MemoryStream(docx));
        using var body = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("BOX Beta", await body.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task EditorNode_RendererThatThrows_ThePageIsStillSavedAndRead()
    {
        await InstallAsync("nodes", failOn: "node");

        var page = await PageAsync(Doc("Alpha"));

        Assert.Equal(1, await FoundAsync("insidetheword")); // the placeholder's words
        Assert.Equal("Before\n\n[probeBox] insidetheword", await MarkdownAsync(page.Id));
    }

    [Fact]
    public async Task EditorNode_ForANodeOfTheEditor_IsNotLoaded()
    {
        await InstallAsync("corenode");

        Assert.Equal((PluginState.DisabledByCore, "paragraph is a node of the editor."), await StatusAsync("kb.test.probe"));
        Assert.Equal("Before\n\n[probeBox] insidetheword", await MarkdownAsync((await PageAsync(Doc("Alpha"))).Id));
    }

    [Fact]
    public async Task EditorNode_ATypeAnotherPluginRenders_IsNotLoaded()
    {
        await InstallAsync("nodes");
        await InstallAsync("nodes", id: "kb.test.probe2");

        Assert.Equal(PluginState.Enabled, (await StatusAsync("kb.test.probe")).State);
        Assert.Equal((PluginState.DisabledByCore, "probeBox already has a renderer."), await StatusAsync("kb.test.probe2"));
    }
}
