using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The search routes go through the action bus: each answers with its action's name, only an Admin gets past the admin handlers, and a plugin can refuse a write and shape a read.</summary>
public sealed class SearchPipelineTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Open File Explorer."}]}]}""";
    private const string Synonyms = "/api/v1/admin/search/synonyms";
    private const string SynonymsKey = "Search.Synonyms";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space;
    private Guid _page;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        var created = await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Map a drive", contentJson = Doc, tags = new[] { "windows" } }, Ct);
        _page = (await created.Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    private static IEnumerable<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name);

    /// <summary>Every admin route of the slice, sent well formed, with the name of its action.</summary>
    private static async Task<List<(string Name, HttpResponseMessage Answer)>> AdminRoutesAsync(HttpClient c) =>
    [
        ("search.synonyms.read", await c.GetAsync(Synonyms, Ct)),
        ("search.synonyms.save", await c.PutAsJsonAsync(Synonyms, new { text = "boardroom, conference room" }, Ct)),
        ("search.reindex", await c.PostAsync("/api/v1/admin/search/reindex", null, Ct)),
    ];

    private async Task<HttpClient> UserAsync()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "u1", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "u1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task EverySearchRoute_AnswersWithItsActionName()
    {
        var search = await _admin.GetAsync("/api/v1/search?q=drive&include=markdown", Ct);
        var markdown = await _admin.GetAsync($"/api/v1/pages/{_page}/markdown", Ct);
        var admin = await AdminRoutesAsync(_admin);

        Assert.Equal((HttpStatusCode.OK, "search.query"), (search.StatusCode, Action(search)));
        Assert.Equal((HttpStatusCode.OK, "page.markdown"), (markdown.StatusCode, Action(markdown)));
        Assert.All(admin, a => Assert.Equal((HttpStatusCode.OK, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        // A refusal by the core names the action too.
        var missing = await _admin.GetAsync($"/api/v1/pages/{Guid.NewGuid()}/markdown", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "page.markdown"), (missing.StatusCode, Action(missing)));

        // The answers are what they were (docs/agent-api.md): the same names in the same order.
        var found = await JsonAsync(search);
        Assert.Equal(["query", "total", "limit", "offset", "hits"], Names(found));
        Assert.Equal(("drive", 1, 20, 0), (found.GetProperty("query").GetString(), found.GetProperty("total").GetInt32(), found.GetProperty("limit").GetInt32(), found.GetProperty("offset").GetInt32()));
        var hit = Assert.Single(found.GetProperty("hits").EnumerateArray());
        Assert.Equal(["id", "spaceId", "spaceName", "kind", "title", "path", "url", "version", "updatedAt", "tags", "snippet", "score", "matched", "text"], Names(hit));
        Assert.Equal((_page, _space, "Service Desk", "Page", "Map a drive", $"/p/{_page}", "1.0", "Open File Explorer."),
            (hit.GetProperty("id").GetGuid(), hit.GetProperty("spaceId").GetGuid(), hit.GetProperty("spaceName").GetString(), hit.GetProperty("kind").GetString(),
                hit.GetProperty("title").GetString(), hit.GetProperty("url").GetString(), hit.GetProperty("version").GetString(), hit.GetProperty("text").GetString()));
        Assert.Equal(["windows"], hit.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["drive"], hit.GetProperty("matched").EnumerateArray().Select(t => t.GetString()));

        var md = await JsonAsync(markdown);
        Assert.Equal(["id", "title", "version", "updatedAt", "markdown"], Names(md));
        Assert.Equal((_page, "Map a drive", "1.0", "Open File Explorer."),
            (md.GetProperty("id").GetGuid(), md.GetProperty("title").GetString(), md.GetProperty("version").GetString(), md.GetProperty("markdown").GetString()));

        Assert.Equal(["text"], Names(await JsonAsync(admin[0].Answer)));
        Assert.Equal("boardroom, conference room", (await JsonAsync(admin[1].Answer)).GetProperty("text").GetString());
        var reindexed = await JsonAsync(admin[2].Answer);
        Assert.Equal(["pages"], Names(reindexed));
        Assert.Equal(1, reindexed.GetProperty("pages").GetInt32());
    }

    [Fact]
    public async Task Search_KeepsItsLimits_AndIsTrimmedToTheCallersSpaces()
    {
        var clamped = await _admin.GetFromJsonAsync<SearchResponse>($"/api/v1/search?q={new string('a', 250)}&limit=500&offset=-3", Ct);
        Assert.Equal((200, 50, 0), (clamped!.Query.Length, clamped.Limit, clamped.Offset));

        Assert.Null(Assert.Single((await _admin.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=drive", Ct))!.Hits).Text); // text only when asked for
        Assert.Equal("Open", Assert.Single((await _admin.GetFromJsonAsync<SearchResponse>("/api/v1/search?tag=windows&include=MARKDOWN&maxChars=1", Ct))!.Hits).Text![..4]); // the cap is 200 at least
        Assert.Empty((await _admin.GetFromJsonAsync<SearchResponse>($"/api/v1/search?q=drive&space={Guid.NewGuid()}", Ct))!.Hits);

        // A signed-in person with no role in the space finds nothing, and the page answers like one that does not exist.
        var user = await UserAsync();
        var none = await user.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=drive", Ct);
        Assert.Equal((0, 0), (none!.Total, none.Hits.Count));
        var hidden = await user.GetAsync($"/api/v1/pages/{_page}/markdown", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "page.markdown"), (hidden.StatusCode, Action(hidden)));
    }

    [Fact]
    public async Task EveryAdminRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        var answers = await AdminRoutesAsync(await UserAsync());

        // The handler refuses, not a filter in front of it: the answer names the action.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        await _f.WithDbAsync(async db => Assert.False(await db.RuntimeSettings.AnyAsync(s => s.Key == SynonymsKey, Ct)));
    }

    [Fact]
    public async Task SaveSynonyms_RefusedByHook_Is422_NothingIsStored()
    {
        await SetAsync("Refuse", "search.synonyms.save");

        var r = await _admin.PutAsJsonAsync(Synonyms, new { text = "boardroom, conference room" }, Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await JsonAsync(r)).GetProperty("detail").GetString());
        await _f.WithDbAsync(async db => Assert.False(await db.RuntimeSettings.AnyAsync(s => s.Key == SynonymsKey, Ct)));
    }

    [Fact]
    public async Task SaveSynonyms_ChecksTheLength_AndStoresWhoSaved()
    {
        var tooLong = await _admin.PutAsJsonAsync(Synonyms, new { text = new string('a', 20001) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("20,000 characters maximum.", (await JsonAsync(tooLong)).GetProperty("errors").GetProperty("text")[0].GetString());
        var none = await _admin.PutAsJsonAsync(Synonyms, new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.True((await JsonAsync(none)).GetProperty("errors").TryGetProperty("text", out _));

        (await _admin.PutAsJsonAsync(Synonyms, new { text = "boardroom, conference room" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal("boardroom, conference room", (await _admin.GetFromJsonAsync<SynonymsDto>(Synonyms, Ct))!.Text);
        await _f.WithDbAsync(async db =>
        {
            var me = (await db.Users.SingleAsync(u => u.IsBreakGlass, Ct)).Id;
            var stored = await db.RuntimeSettings.SingleAsync(s => s.Key == SynonymsKey, Ct);
            Assert.Equal(("boardroom, conference room", me), (stored.Value, stored.UpdatedBy));
            Assert.False(await db.AuditEntries.AnyAsync(a => EF.Functions.Like(a.Action, "search.%"), Ct)); // no audit entry, as before
        });
    }

    [Fact]
    public async Task Reads_FilterBlanksAText()
    {
        await SetAsync("Blank", "page.markdown:Markdown");
        var md = await _admin.GetFromJsonAsync<PageMarkdownDto>($"/api/v1/pages/{_page}/markdown", Ct);
        Assert.Equal(("Map a drive", ""), (md!.Title, md.Markdown));

        await SetAsync("Blank", "search.query:Query");
        var found = await _admin.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=drive", Ct);
        Assert.Equal(("", 1), (found!.Query, found.Hits.Count));
        // The filter shapes the answer, not the page.
        Assert.Equal("Open File Explorer.", (await _admin.GetFromJsonAsync<PageMarkdownDto>($"/api/v1/pages/{_page}/markdown", Ct))!.Markdown);
    }
}
