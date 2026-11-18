using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Pages, the tree, versions, a space's content and trash, macros and body conversion (contracts/behaviour/content.md and the rest).</summary>
public sealed class DcContentTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space;
    private HttpClient _olga = null!, _cora = null!, _rita = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var (user, role) in new[] { ("olga", "Owner"), ("cora", "Contributor"), ("rita", "Reader") })
        {
            var id = await UserAsync(_f, user);
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        }
        (_olga, _cora, _rita) = (Basic(_f, "olga"), Basic(_f, "cora"), Basic(_f, "rita"));
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static object Page(string title, string storage = "<p>Body</p>", string? parent = null, string space = "OPS") => parent is null
        ? new { type = "page", title, space = new { key = space }, body = new { storage = new { value = storage, representation = "storage" } } }
        : new { type = "page", title, space = new { key = space }, ancestors = new[] { new { id = parent } }, body = new { storage = new { value = storage, representation = "storage" } } };

    private async Task<string> CreateAsync(string title, string storage = "<p>Body</p>", string? parent = null, HttpClient? by = null) =>
        (await (await (by ?? _cora).PostJsonAsync("/rest/api/content", Page(title, storage, parent))).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;

    private static object Update(int number, string title, string? storage = null, bool minor = false) => storage is null
        ? new { type = "page", title, version = new { number, minorEdit = minor } }
        : new { type = "page", title, version = new { number, message = "note " + number, minorEdit = minor }, body = new { storage = new { value = storage, representation = "storage" } } };

    private static string Storage(JsonElement c) => c.GetProperty("body").GetProperty("storage").GetProperty("value").GetString()!;
    private static IEnumerable<string?> Titles(JsonElement page) => page.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("title").GetString());

    [Fact]
    public async Task History_IsForMembers_NotForWhoReadsOnlyBecauseOfVisibility()
    {
        var id = await CreateAsync("Open runbook", "<p>1</p><ac:structured-macro ac:name=\"info\" ac:macro-id=\"m-1\"><ac:rich-text-body><p>x</p></ac:rich-text-body></ac:structured-macro>");
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", Update(2, "Open runbook", "<p>2</p>"), Ct)).JsonAsync(HttpStatusCode.OK);
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { visibility = "SignedIn" }, Ct)).EnsureSuccessStatusCode();
        await UserAsync(_f, "nils");
        var nils = Basic(_f, "nils");

        // Not a member: the current page, but no history, no old version, no macro of an old version.
        Assert.Equal("<p>2</p>", Storage(await nils.GetJsonAsync($"/rest/api/content/{id}?expand=body.storage")));
        Assert.Equal("<p>2</p>", Storage(await nils.GetJsonAsync($"/rest/api/content/{id}?version=2&expand=body.storage")));
        await (await nils.GetAsync($"/rest/api/content/{id}/history", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await nils.GetAsync($"/rest/api/content/{id}?version=1", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await nils.GetAsync($"/rest/api/content/{id}/history/1/macro/id/m-1", Ct)).ErrorAsync(HttpStatusCode.Forbidden);

        // A Reader of the space keeps it.
        Assert.True((await _rita.GetJsonAsync($"/rest/api/content/{id}/history")).GetProperty("latest").GetBoolean());
        Assert.StartsWith("<p>1</p>", Storage(await _rita.GetJsonAsync($"/rest/api/content/{id}?version=1&expand=body.storage")), StringComparison.Ordinal);
        Assert.Equal("info", (await _rita.GetJsonAsync($"/rest/api/content/{id}/history/1/macro/id/m-1")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Create_StoresTheBody_AndReadsBackAsWritten()
    {
        const string body = "<h2>Steps</h2><p>Run <code>reset</code> <ac:link><ri:page ri:content-title=\"Other\" /></ac:link></p><ac:structured-macro ac:name=\"jira\" ac:macro-id=\"j1\"><ac:parameter ac:name=\"key\">OPS-1</ac:parameter></ac:structured-macro>";
        var created = await (await _cora.PostJsonAsync("/rest/api/content", Page("Release checklist", body))).JsonAsync(HttpStatusCode.OK);
        var id = created.GetProperty("id").GetString()!;
        Assert.Matches("^[0-9]+$", id);
        Assert.Equal("page", created.GetProperty("type").GetString());
        Assert.Equal("current", created.GetProperty("status").GetString());
        Assert.Equal(1, created.GetProperty("version").GetProperty("number").GetInt32());
        Assert.Equal("OPS", created.GetProperty("space").GetProperty("key").GetString());
        Assert.True(created.TryGetProperty("history", out _));
        Assert.Equal("/rest/api/content", created.GetProperty("_links").GetProperty("collection").GetString());

        var read = await _rita.GetJsonAsync($"/rest/api/content/{id}?expand=body.storage,body.view,ancestors");
        Assert.Equal(body, Storage(read));
        var view = read.GetProperty("body").GetProperty("view").GetProperty("value").GetString()!;
        Assert.Contains("<h2>Steps</h2>", view, StringComparison.Ordinal);
        Assert.Contains("aerie-opaque", view, StringComparison.Ordinal); // the macro aerie does not render
        Assert.Contains("aerie-unresolved", view, StringComparison.Ordinal); // a link to a page that is not there
        Assert.Equal(0, read.GetProperty("ancestors").GetArrayLength());
        Assert.False(read.TryGetProperty("space", out _)); // sending expand replaces the defaults
        Assert.Equal("", read.GetProperty("_expandable").GetProperty("version").GetString());

        // The page is aerie's own: its UUID reaches it too, and the answer carries the compatible id.
        var uuid = (await _f.WithDbValueAsync(db => db.Pages.Where(p => p.Title == "Release checklist").Select(p => p.Id).SingleAsync(Ct)));
        Assert.Equal(id, (await _rita.GetJsonAsync($"/rest/api/content/{uuid}")).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Create_Refusals()
    {
        await CreateAsync("Taken");
        await (await _cora.PostJsonAsync("/rest/api/content", Page("TAKEN"))).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync("/rest/api/content", Page("x", "<p>open"))).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync("/rest/api/content", new { type = "blogpost", title = "B", space = new { key = "OPS" } })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync("/rest/api/content", new { type = "comment", container = new { id = "1", type = "page" } })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync("/rest/api/content", new { type = "page", space = new { key = "OPS" } })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostAsJsonAsync("/rest/api/content?status=draft", Page("Draft"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync("/rest/api/content", new { type = "page", title = "V", space = new { key = "OPS" }, body = new { view = new { value = "<p/>", representation = "view" } } })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync("/rest/api/content", Page("Nowhere", space: "NOPE"))).ErrorAsync(HttpStatusCode.NotFound);
        await (await _cora.PostJsonAsync("/rest/api/content", Page("Orphan", parent: "999999"))).ErrorAsync(HttpStatusCode.NotFound);
        await (await _rita.PostJsonAsync("/rest/api/content", Page("Reader's"))).ErrorAsync(HttpStatusCode.Forbidden);
        await (await _f.NewClient().PostJsonAsync("/rest/api/content", Page("Nobody's"))).ErrorAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.NoContent, (await _olga.PutAsync("/rest/api/space/OPS/archive", null, Ct)).StatusCode);
        await (await _cora.PostJsonAsync("/rest/api/content", Page("Archived"))).ErrorAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APageThatGainsAChild_BecomesAFolder_InPlace()
    {
        var parent = await CreateAsync("Runbooks", "<p>Index</p>");
        var child = await CreateAsync("Restart", parent: parent);
        var deep = await CreateAsync("Deep", parent: child);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.CountAsync(a => a.Action == AuditActions.PageToFolder, Ct) == 2));

        var p = await _rita.GetJsonAsync($"/rest/api/content/{parent}?expand=body.storage,version,children.page");
        Assert.Equal("<p>Index</p>", Storage(p)); // the body stays
        Assert.Equal(1, p.GetProperty("version").GetProperty("number").GetInt32());
        Assert.Equal(["Restart"], Titles(p.GetProperty("children").GetProperty("page")));

        var c = await _rita.GetJsonAsync($"/rest/api/content/{child}?expand=ancestors");
        Assert.Equal([parent], c.GetProperty("ancestors").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.Equal(2, (await _rita.GetJsonAsync($"/rest/api/content/{parent}/descendant/page/count")).GetProperty("count").GetInt64());
        Assert.Equal(["Restart", "Deep"], Titles(await _rita.GetJsonAsync($"/rest/api/content/{parent}/descendant/page")));
        var map = await _rita.GetJsonAsync($"/rest/api/content/{parent}/child?expand=page");
        Assert.Equal(["Restart"], Titles(map.GetProperty("page")));
        Assert.True(map.GetProperty("_expandable").TryGetProperty("comment", out _));
        Assert.Equal(0, (await _rita.GetJsonAsync($"/rest/api/content/{parent}/child/comment?depth=all")).GetProperty("size").GetInt32());
        await (await _rita.GetAsync($"/rest/api/content/{parent}/child/folder", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync($"/rest/api/content/{parent}/child?parentVersion=2", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync($"/rest/api/content/{parent}/child/comment?location=side", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        Assert.Equal(0, (await _rita.GetJsonAsync($"/rest/api/content/{deep}/child/page")).GetProperty("size").GetInt32()); // a page holds no pages
        Assert.Equal(0, (await _rita.GetJsonAsync($"/rest/api/content/{deep}/descendant/page/count")).GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Update_NeedsTheNextVersion_AndMakesOne()
    {
        var id = await CreateAsync("Checklist", "<p>v1</p>");
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", Update(3, "Checklist", "<p>skip</p>"), Ct)).ErrorAsync(HttpStatusCode.Conflict);
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}?conflictPolicy=overwrite", Update(2, "Checklist"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", new { type = "blogpost", title = "x", version = new { number = 2 } }, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", new { type = "page", title = "x" }, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.PutAsJsonAsync($"/rest/api/content/{id}", Update(2, "Checklist"), Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await _cora.PutAsJsonAsync("/rest/api/content/999999", Update(2, "Checklist"), Ct)).ErrorAsync(HttpStatusCode.NotFound);

        var v2 = await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}?asyncReconciliation=true&expand=body.storage,version", Update(2, "Checklist v2", "<p>v2</p>"), Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("Checklist v2", "<p>v2</p>", 2, "note 2"), (v2.GetProperty("title").GetString(), Storage(v2), v2.GetProperty("version").GetProperty("number").GetInt32(), v2.GetProperty("version").GetProperty("message").GetString()));
        // Fields left out stay: no body keeps the body.
        var v3 = await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}?expand=body.storage", Update(3, "Checklist v3", minor: true), Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("<p>v2</p>", Storage(v3));

        var old = await _rita.GetJsonAsync($"/rest/api/content/{id}?version=1&expand=body.storage");
        Assert.Equal(("historical", "Checklist", "<p>v1</p>"), (old.GetProperty("status").GetString(), old.GetProperty("title").GetString(), Storage(old)));
        await (await _rita.GetAsync($"/rest/api/content/{id}?version=9", Ct)).ErrorAsync(HttpStatusCode.NotFound);

        var other = await CreateAsync("Other");
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{other}", Update(2, "checklist V3"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_MovesUnderANewParent()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var moved = await (await _cora.PutAsJsonAsync($"/rest/api/content/{b}?expand=ancestors", new { type = "page", title = "B", version = new { number = 2 }, ancestors = new[] { new { id = a } } }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal([a], moved.GetProperty("ancestors").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{a}", new { type = "page", title = "A", version = new { number = 2 }, ancestors = new[] { new { id = b } } }, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        // A folder keeps versions of its own body through the surface.
        var folder = await (await _cora.PutAsJsonAsync($"/rest/api/content/{a}?expand=body.storage,version", Update(2, "A", "<p>folder text</p>"), Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("<p>folder text</p>", 2), (Storage(folder), folder.GetProperty("version").GetProperty("number").GetInt32()));
        Assert.Equal("<p>Body</p>", Storage(await _rita.GetJsonAsync($"/rest/api/content/{a}?version=1&expand=body.storage")));
    }

    [Fact]
    public async Task Trash_Restore_AndPurge()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parent: parent);
        await (await _rita.DeleteAsync($"/rest/api/content/{parent}", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"/rest/api/content/{parent}", Ct)).StatusCode);
        await (await _rita.GetAsync($"/rest/api/content/{parent}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        // Its child stays, moved up to the top of the space.
        Assert.Equal(0, (await _rita.GetJsonAsync($"/rest/api/content/{child}?expand=ancestors")).GetProperty("ancestors").GetArrayLength());
        Assert.Equal("trashed", (await _olga.GetJsonAsync($"/rest/api/content/{parent}?status=trashed")).GetProperty("status").GetString());
        await (await _rita.GetAsync($"/rest/api/content/{parent}?status=any", Ct)).ErrorAsync(HttpStatusCode.NotFound); // only who manages the bin
        await (await _olga.DeleteAsync($"/rest/api/content/{parent}", Ct)).ErrorAsync(HttpStatusCode.NotFound); // trashed needs status=trashed

        var trash = await _olga.GetJsonAsync("/rest/api/space/ops/trash");
        Assert.Equal(["Parent"], Titles(trash));
        await (await _rita.GetAsync("/rest/api/space/OPS/trash", Ct)).ErrorAsync(HttpStatusCode.Forbidden);

        var back = await (await _olga.PutAsJsonAsync($"/rest/api/content/{parent}?status=trashed", new { type = "page", title = "Parent", status = "current", version = new { number = 2 } }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("current", 1), (back.GetProperty("status").GetString(), back.GetProperty("version").GetProperty("number").GetInt32())); // restoring makes no version

        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"/rest/api/content/{parent}", Ct)).StatusCode);
        await (await _cora.DeleteAsync($"/rest/api/content/{parent}?status=trashed", Ct)).ErrorAsync(HttpStatusCode.NotFound); // the trash is seen by its managers only
        Assert.Equal(HttpStatusCode.NoContent, (await _olga.DeleteAsync($"/rest/api/content/{parent}?status=trashed", Ct)).StatusCode);
        await (await _olga.GetAsync($"/rest/api/content/{parent}?status=trashed", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.PagePurge, Ct)));

        await CreateAsync("Gone soon");
        var gone = (await _olga.GetJsonAsync("/rest/api/content?title=gone%20SOON")).GetProperty("results")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"/rest/api/content/{gone}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _olga.DeleteAsync("/rest/api/space/OPS/trash", Ct)).StatusCode);
        Assert.Equal(0, (await _olga.GetJsonAsync("/rest/api/space/OPS/trash")).GetProperty("size").GetInt32());
        await (await _cora.DeleteAsync("/rest/api/space/OPS/trash", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task History_And_VersionRemoval()
    {
        var id = await CreateAsync("Notes", "<p>1</p>");
        for (var n = 2; n <= 4; n++) (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", Update(n, "Notes", $"<p>{n}</p>"), Ct)).EnsureSuccessStatusCode();
        var h = await _rita.GetJsonAsync($"/rest/api/content/{id}/history?expand=lastUpdated,previousVersion,contributors");
        Assert.True(h.GetProperty("latest").GetBoolean());
        Assert.Equal("cora", h.GetProperty("createdBy").GetProperty("username").GetString());
        Assert.Equal(4, h.GetProperty("lastUpdated").GetProperty("number").GetInt32());
        Assert.Equal(3, h.GetProperty("previousVersion").GetProperty("number").GetInt32());
        Assert.False(h.TryGetProperty("nextVersion", out _));
        Assert.Equal(1, h.GetProperty("contributors").GetProperty("publishers").GetProperty("userKeys").GetArrayLength());

        await (await _cora.DeleteAsync($"/rest/api/content/{id}/version/4", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.DeleteAsync($"/rest/api/content/{id}/version/2", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"/rest/api/content/{id}/version/2", Ct)).StatusCode);
        await (await _rita.GetAsync($"/rest/api/content/{id}?version=2", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _cora.DeleteAsync($"/rest/api/content/{id}/version/2", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _cora.DeleteAsync($"/rest/api/content/{id}/version/x", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        // Numbers do not shift: 1, 3, 4 remain, and the next is 5.
        Assert.Equal("<p>3</p>", Storage(await _rita.GetJsonAsync($"/rest/api/content/{id}?version=3&expand=body.storage")));
        Assert.Equal(5, (await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", Update(5, "Notes"), Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("version").GetProperty("number").GetInt32());
        var uuid = await _f.WithDbValueAsync(db => db.Pages.Where(p => p.Title == "Notes").Select(p => p.Id).SingleAsync(Ct));
        Assert.Equal(5, await _f.WithDbValueAsync(db => db.PageVersions.CountAsync(v => v.PageId == uuid, Ct))); // aerie keeps every version
    }

    [Fact]
    public async Task List_Filters_AndPages_InIdOrder()
    {
        var a = await CreateAsync("Alpha");
        var b = await CreateAsync("Beta");
        await CreateAsync("Gamma");
        Assert.Equal(["Alpha", "Beta", "Gamma"], Titles(await _rita.GetJsonAsync("/rest/api/content?spaceKey=ops")));
        Assert.Equal(["Beta"], Titles(await _rita.GetJsonAsync("/rest/api/content?title=BETA")));
        Assert.Equal(["Alpha", "Beta"], Titles(await _rita.GetJsonAsync($"/rest/api/content?ids={b}&ids={a}&type=blogpost")));
        Assert.Empty(Titles(await _rita.GetJsonAsync("/rest/api/content?type=blogpost&postingDay=2026-03-03")));
        var first = await _rita.GetJsonAsync("/rest/api/content?limit=2");
        Assert.Equal(["Alpha", "Beta"], Titles(first));
        Assert.Equal(["Gamma"], Titles(await _rita.GetJsonAsync(first.GetProperty("_links").GetProperty("next").GetString()!)));
        await (await _rita.GetAsync("/rest/api/content?type=comment", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync("/rest/api/content?status=gone", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync("/rest/api/content?spaceKey=NOPE", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _rita.GetAsync("/rest/api/content?limit=0", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        Assert.Empty(Titles(await _f.NewClient().GetJsonAsync("/rest/api/content")));
    }

    [Fact]
    public async Task Scan_WalksEveryItemOnce()
    {
        foreach (var t in new[] { "One", "Two", "Three", "Four", "Five" }) await CreateAsync(t);
        var seen = new List<string?>();
        var page = await _rita.GetJsonAsync("/rest/api/content/scan?limit=2&spaceKey=OPS");
        Assert.False(page.TryGetProperty("prevCursor", out _));
        while (true)
        {
            seen.AddRange(Titles(page));
            if (!page.TryGetProperty("nextCursor", out var next)) break;
            Assert.True(page.GetProperty("_links").TryGetProperty("next", out _));
            page = await _rita.GetJsonAsync("/rest/api/content/scan?limit=2&cursor=" + next.GetString());
            Assert.True(page.TryGetProperty("prevCursor", out _));
        }
        Assert.Equal(["One", "Two", "Three", "Four", "Five"], seen);
        var back = await _rita.GetJsonAsync("/rest/api/content/scan?limit=2&cursor=" + page.GetProperty("prevCursor").GetString());
        Assert.Equal(["Three", "Four"], Titles(back));
        Assert.Empty(Titles(await _rita.GetJsonAsync("/rest/api/content/scan?type=comment")));
        await (await _rita.GetAsync("/rest/api/content/scan?cursor=%%%", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync("/rest/api/content/scan?type=attachment", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SpaceContent_ByTypeAndDepth()
    {
        var top = await CreateAsync("Top");
        await CreateAsync("Under", parent: top);
        var map = await _rita.GetJsonAsync("/rest/api/space/OPS/content");
        Assert.Equal(["Top", "Under"], Titles(map.GetProperty("page")));
        Assert.Equal(0, map.GetProperty("blogpost").GetProperty("size").GetInt32());
        Assert.Equal(["Top"], Titles(await _rita.GetJsonAsync("/rest/api/space/ops/content/page?depth=root")));
        Assert.Empty(Titles(await _rita.GetJsonAsync("/rest/api/space/OPS/content/blogpost")));
        await (await _rita.GetAsync("/rest/api/space/OPS/content/comment", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync("/rest/api/space/OPS/content?depth=deep", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync("/rest/api/space/NOPE/content", Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Macros_ByIdAndByHash()
    {
        const string macro = "<ac:structured-macro ac:name=\"info\" ac:macro-id=\"m-1\"><ac:parameter ac:name=\"title\">Heads up</ac:parameter><ac:rich-text-body><p>Careful</p></ac:rich-text-body></ac:structured-macro>";
        var id = await CreateAsync("With macro", "<p>x</p>" + macro);
        var byId = await _rita.GetJsonAsync($"/rest/api/content/{id}/history/1/macro/id/m-1");
        Assert.Equal(("info", "<p>Careful</p>", "Heads up"), (byId.GetProperty("name").GetString(), byId.GetProperty("body").GetString(), byId.GetProperty("parameters").GetProperty("title").GetString()));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(macro)));
        Assert.Equal("info", (await _rita.GetJsonAsync($"/rest/api/content/{id}/history/1/macro/hash/{hash}")).GetProperty("name").GetString());
        await (await _rita.GetAsync($"/rest/api/content/{id}/history/1/macro/id/nope", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _rita.GetAsync($"/rest/api/content/{id}/history/7/macro/id/m-1", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        // The view renders the T1 macros.
        Assert.Contains("aerie-panel-info", (await _rita.GetJsonAsync($"/rest/api/content/{id}?expand=body.view")).GetProperty("body").GetProperty("view").GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertBody_SupportedPairs()
    {
        var view = await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/view", new { value = "<p>Hello <strong>team</strong></p>", representation = "storage" })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("<p>Hello <strong>team</strong></p>", "view"), (view.GetProperty("value").GetString(), view.GetProperty("representation").GetString()));
        var styled = await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/styled_view", new { value = "<p>x</p>", representation = "storage" })).JsonAsync(HttpStatusCode.OK);
        Assert.StartsWith("<!DOCTYPE html><html>", styled.GetProperty("value").GetString(), StringComparison.Ordinal);
        var storage = await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/storage", new { value = "<p>a<br>b</p>", representation = "editor" })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("<p>a<br />b</p>", storage.GetProperty("value").GetString());
        await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/storage", new { value = "<p/>", representation = "storage" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/plain", new { value = "<p/>", representation = "storage" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/view", new { value = "<p>", representation = "storage" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _f.NewClient().PostJsonAsync("/rest/api/contentbody/convert/view", new { value = "<p/>", representation = "storage" })).ErrorAsync(HttpStatusCode.Unauthorized);
        // On a page: the children macro lists its children.
        var parent = await CreateAsync("Hub");
        await CreateAsync("Spoke", parent: parent);
        var children = await (await _rita.PostJsonAsync("/rest/api/contentbody/convert/view", new { value = "<ac:structured-macro ac:name=\"children\" />", representation = "storage", content = new { id = parent } })).JsonAsync(HttpStatusCode.OK);
        Assert.Contains("Spoke", children.GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagesMadeInAerie_ShowWithIdsAndStorage()
    {
        var made = await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Native",
            contentJson = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","marks":[{"type":"bold"}],"text":"Hi"}]}]}""" }, Ct)).JsonAsync(HttpStatusCode.Created);
        var uuid = made.GetProperty("id").GetGuid();
        var c = await _rita.GetJsonAsync($"/rest/api/content/{uuid}?expand=body.storage,version");
        Assert.Equal("<p><strong>Hi</strong></p>", Storage(c));
        Assert.Equal(1, c.GetProperty("version").GetProperty("number").GetInt32());
        Assert.Matches("^[0-9]+$", c.GetProperty("id").GetString()!);
    }
}
