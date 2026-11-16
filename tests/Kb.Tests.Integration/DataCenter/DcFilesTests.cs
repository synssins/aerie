using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Attachments, labels and CQL search (contracts/behaviour/attachments.md, labels-properties.md, search-cql.md).</summary>
public sealed class DcFilesTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _cora = null!, _rita = null!;
    private Guid _space;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var (user, role) in new[] { ("cora", "Contributor"), ("rita", "Reader") })
        {
            var id = await UserAsync(_f, user);
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        }
        (_cora, _rita) = (Basic(_f, "cora"), Basic(_f, "rita"));
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<string> PageAsync(string title, string body = "<p>Body</p>", string? parent = null)
    {
        object page = parent is null
            ? new { type = "page", title, space = new { key = "OPS" }, body = new { storage = new { value = body, representation = "storage" } } }
            : new { type = "page", title, space = new { key = "OPS" }, ancestors = new[] { new { id = parent } }, body = new { storage = new { value = body, representation = "storage" } } };
        return (await (await _cora.PostJsonAsync("/rest/api/content", page)).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;
    }

    private static MultipartFormDataContent Files(params (string Name, string Text, string? Type)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, text, type) in files)
        {
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            if (type is not null) part.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(part, "file", name);
        }
        return form;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, string path, HttpContent form, bool xsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        if (xsrf) req.Headers.Add("X-Atlassian-Token", "no-check");
        return c.SendAsync(req, Ct);
    }

    private static IEnumerable<string?> Titles(JsonElement page) => page.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("title").GetString());

    [Fact]
    public async Task Upload_List_Download_AndVersions()
    {
        var page = await PageAsync("Runbook");
        var path = $"/rest/api/content/{page}/child/attachment";
        await (await UploadAsync(_cora, path, Files(("a.txt", "x", null)), xsrf: false)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await UploadAsync(_rita, path, Files(("a.txt", "x", null)))).ErrorAsync(HttpStatusCode.Forbidden);
        await (await UploadAsync(_cora, "/rest/api/content/999999/child/attachment", Files(("a.txt", "x", null)))).ErrorAsync(HttpStatusCode.NotFound);

        var form = Files(("notes.txt", "first notes", "text/plain"), ("data.json", "{}", null));
        form.Add(new StringContent("one"), "comment");
        form.Add(new StringContent("two"), "comment");
        var created = await (await UploadAsync(_cora, path, form)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(2, created.GetProperty("size").GetInt32());
        var notes = created.GetProperty("results")[0];
        var id = notes.GetProperty("id").GetString()!;
        Assert.Equal(("attachment", "notes.txt", "text/plain", "one"), (notes.GetProperty("type").GetString(), notes.GetProperty("title").GetString(),
            notes.GetProperty("extensions").GetProperty("mediaType").GetString(), notes.GetProperty("extensions").GetProperty("comment").GetString()));
        Assert.Equal("application/json", created.GetProperty("results")[1].GetProperty("extensions").GetProperty("mediaType").GetString()); // guessed from the name
        Assert.Equal(11, notes.GetProperty("extensions").GetProperty("fileSize").GetInt64());

        var badComments = Files(("c.txt", "c", null));
        badComments.Add(new StringContent("1"), "comment");
        badComments.Add(new StringContent("2"), "comment");
        await (await UploadAsync(_cora, path, badComments)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await UploadAsync(_cora, path, Files(("notes.txt", "again", null)))).ErrorAsync(HttpStatusCode.BadRequest); // the name is taken

        var list = await _rita.GetJsonAsync(path);
        Assert.Equal(50, list.GetProperty("limit").GetInt32());
        Assert.Equal(["notes.txt", "data.json"], Titles(list));
        Assert.Equal(["notes.txt"], Titles(await _rita.GetJsonAsync(path + "?filename=notes.txt")));
        Assert.Empty(Titles(await _rita.GetJsonAsync(path + "?filename=NOTES.txt")));
        Assert.Equal(["data.json"], Titles(await _rita.GetJsonAsync(path + "?mediaType=application/json")));
        Assert.Equal(["notes.txt"], Titles(await _rita.GetJsonAsync($"/rest/api/content/{page}/child/attachment?limit=1")));
        Assert.Equal(2, (await _rita.GetJsonAsync($"/rest/api/content/{page}?expand=children.attachment")).GetProperty("children").GetProperty("attachment").GetProperty("size").GetInt32());
        Assert.Equal("attachment", (await _rita.GetJsonAsync($"/rest/api/content/{id}")).GetProperty("type").GetString());

        // A new version with allowDuplicated, then new bytes through /data.
        var v2 = await (await UploadAsync(_cora, path + "?allowDuplicated=true", Files(("notes.txt", "second notes!", null)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(id, v2.GetProperty("results")[0].GetProperty("id").GetString());
        var v3 = await (await UploadAsync(_cora, $"{path}/{id}/data", Files(("notes.txt", "third", null)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(5, v3.GetProperty("extensions").GetProperty("fileSize").GetInt64());
        var download = v3.GetProperty("_links").GetProperty("download").GetString()!;
        Assert.Equal("third", await _rita.GetStringAsync(download, Ct));
        Assert.Equal("first notes", await _rita.GetStringAsync(download + "?version=1", Ct));
        Assert.Equal("second notes!", await _rita.GetStringAsync(download + "?version=2", Ct));
        var r = await _rita.GetAsync(download, Ct);
        Assert.Equal("text/plain", r.Content.Headers.ContentType!.MediaType);
        await (await Basic(_f, "nobody-here").GetAsync(download, Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        // aerie's own download serves the current file.
        var uuid = await _f.WithDbValueAsync(db => db.Attachments.Where(a => a.FileName == "notes.txt").Select(a => a.Id).SingleAsync(Ct));
        Assert.Equal("third", await _admin.GetStringAsync($"/api/v1/attachments/{uuid}", Ct));

        // Removing the current version brings the one before back.
        await (await _rita.DeleteAsync($"{path}/{id}/version/3", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"{path}/{id}/version/3", Ct)).StatusCode);
        Assert.Equal("second notes!", await _rita.GetStringAsync(download, Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"{path}/{id}/version/1", Ct)).StatusCode);
        await (await _cora.DeleteAsync($"{path}/{id}/version/2", Ct)).ErrorAsync(HttpStatusCode.BadRequest); // the only one left
        await (await _cora.DeleteAsync($"{path}/{id}/version/9", Ct)).ErrorAsync(HttpStatusCode.NotFound);

        var text = await _rita.GetJsonAsync($"{path}/{id}/extractedtext?limit=6");
        Assert.Equal("second", text.GetProperty("extractedText").GetString());
        await (await _rita.GetAsync($"{path}/{id}/extractedtext?limit=0", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Properties_Move_AndTrash()
    {
        var page = await PageAsync("Source");
        var other = await PageAsync("Target");
        var path = $"/rest/api/content/{page}/child/attachment";
        var id = (await (await UploadAsync(_cora, path, Files(("a.txt", "aaa", null)))).JsonAsync(HttpStatusCode.OK)).GetProperty("results")[0].GetProperty("id").GetString()!;
        await UploadAsync(_cora, path, Files(("b.txt", "bbb", null)));

        await (await _cora.PutAsJsonAsync($"{path}/{id}", new { id, type = "attachment", title = "a2.txt", version = new { number = 3 } }, Ct)).ErrorAsync(HttpStatusCode.Conflict);
        await (await _cora.PutAsJsonAsync($"{path}/{id}", new { id, type = "attachment", title = "b.txt", version = new { number = 2 } }, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        var renamed = await (await _cora.PutAsJsonAsync($"{path}/{id}", new { id, type = "attachment", title = "a2.txt", version = new { number = 2 }, metadata = new { mediaType = "text/csv", comment = "renamed" } }, Ct))
            .JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("a2.txt", "text/csv", "renamed"), (renamed.GetProperty("title").GetString(), renamed.GetProperty("extensions").GetProperty("mediaType").GetString(), renamed.GetProperty("extensions").GetProperty("comment").GetString()));
        Assert.Equal("aaa", await _rita.GetStringAsync(renamed.GetProperty("_links").GetProperty("download").GetString()!, Ct)); // the bytes stay

        var move = new HttpRequestMessage(HttpMethod.Post, $"{path}/{id}/move?newContentId={other}&newName=moved.txt");
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.SendAsync(move, Ct)).StatusCode);
        await (await _rita.GetAsync($"{path}/{id}", Ct)).ErrorAsync(HttpStatusCode.MethodNotAllowed);
        Assert.Equal(["moved.txt"], Titles(await _rita.GetJsonAsync($"/rest/api/content/{other}/child/attachment")));
        await (await _cora.PostAsync($"{path}/{id}/move?newName=x.txt", null, Ct)).ErrorAsync(HttpStatusCode.NotFound); // it is not on that content any more
        await (await _cora.PostAsync($"/rest/api/content/{other}/child/attachment/{id}/move", null, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.AttachmentMove, Ct)));

        await (await _rita.DeleteAsync($"/rest/api/content/{other}/child/attachment/{id}", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"/rest/api/content/{other}/child/attachment/att{id}", Ct)).StatusCode);
        Assert.Empty(Titles(await _rita.GetJsonAsync($"/rest/api/content/{other}/child/attachment")));
        await (await _cora.DeleteAsync($"/rest/api/content/{other}/child/attachment/{id}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Folders_TakeAttachments_AndTheLimitHolds()
    {
        // attachments.md: clients cannot tell folders from pages, so both take attachments.
        var folder = await PageAsync("Folder");
        await PageAsync("Inside", parent: folder);
        var path = $"/rest/api/content/{folder}/child/attachment";
        var made = await (await UploadAsync(_cora, path, Files(("f.txt", "folder file", null)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["f.txt"], Titles(await _rita.GetJsonAsync(path)));
        Assert.Equal("folder file", await _rita.GetStringAsync(made.GetProperty("results")[0].GetProperty("_links").GetProperty("download").GetString()!, Ct));
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.AttachmentAdd, Ct)));
        // And one moves onto a folder.
        var page = await PageAsync("Small");
        var other = (await (await UploadAsync(_cora, $"/rest/api/content/{page}/child/attachment", Files(("p.txt", "page file", null)))).JsonAsync(HttpStatusCode.OK)).GetProperty("results")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.PostAsync($"/rest/api/content/{page}/child/attachment/{other}/move?newContentId={folder}", null, Ct)).StatusCode);
        Assert.Equal(["f.txt", "p.txt"], Titles(await _rita.GetJsonAsync(path)));
        (await _admin.PutAsJsonAsync("/api/v1/admin/uploads", new { attachmentMb = 1 }, Ct)).EnsureSuccessStatusCode();
        await (await UploadAsync(_cora, $"/rest/api/content/{page}/child/attachment", Files(("big.txt", new string('x', 1_100_000), null)))).ErrorAsync(HttpStatusCode.RequestEntityTooLarge);
        await (await UploadAsync(_cora, $"/rest/api/content/{page}/child/attachment", new StringContent("{}", Encoding.UTF8, "application/json"))).ErrorAsync(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Labels_AddListRemove()
    {
        var page = await PageAsync("Labelled");
        var path = $"/rest/api/content/{page}/label";
        var after = await (await _cora.PostJsonAsync(path, new object[] { new { prefix = "global", name = "Release" }, new { name = "checklist" }, new { name = "global:ops" } })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(["checklist", "ops", "release"], after.GetProperty("results").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.All(after.GetProperty("results").EnumerateArray(), l => Assert.Matches("^[0-9]+$", l.GetProperty("id").GetString()!));
        Assert.Equal(3, (await (await _cora.PostJsonAsync(path, new { name = "release" })).JsonAsync(HttpStatusCode.OK)).GetProperty("size").GetInt32()); // already there: nothing added
        await (await _cora.PostJsonAsync(path, new object[] { new { name = "fine" }, new { name = "not ok" } })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync(path, new { prefix = "my", name = "todo" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PostJsonAsync(path, new { prefix = "other", name = "x" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.PostJsonAsync(path, new { name = "nope" })).ErrorAsync(HttpStatusCode.Forbidden);

        var list = await _rita.GetJsonAsync(path);
        Assert.Equal(200, list.GetProperty("limit").GetInt32());
        Assert.Equal(3, list.GetProperty("size").GetInt32());
        Assert.Equal(0, (await _rita.GetJsonAsync(path + "?prefix=my")).GetProperty("size").GetInt32());
        Assert.Equal(["checklist"], (await _rita.GetJsonAsync(path + "?limit=1")).GetProperty("results").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal("release", (await _rita.GetJsonAsync($"/rest/api/content/{page}?expand=metadata.labels")).GetProperty("metadata").GetProperty("labels").GetProperty("results")[2].GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"{path}/release", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _cora.DeleteAsync($"{path}?name=global:ops", Ct)).StatusCode);
        await (await _cora.DeleteAsync($"{path}/release", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _cora.DeleteAsync(path, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.DeleteAsync($"{path}/checklist", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal(["checklist"], (await _rita.GetJsonAsync(path)).GetProperty("results").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.CountAsync(a => a.Action == AuditActions.PageLabels, Ct) >= 3));
    }

    [Fact]
    public async Task Search_Cql()
    {
        var root = await PageAsync("Release notes", "<p>The rollback plan for every release.</p>");
        var child = await PageAsync("Rollback steps", "<p>Restore the colour database.</p>", parent: root);
        await PageAsync("Lunch menu", "<p>Soup and bread.</p>");
        await _cora.PostJsonAsync($"/rest/api/content/{child}/label", new { name = "runbook" });

        async Task<IEnumerable<string?>> Find(string cql, string path = "/rest/api/content/search") => Titles(await _rita.GetJsonAsync($"{path}?cql={Uri.EscapeDataString(cql)}"));

        Assert.Equal(["Release notes", "Rollback steps"], await Find("type = page AND text ~ rollback ORDER BY title"));
        Assert.Equal(["Rollback steps"], await Find("label = runbook"));
        Assert.Equal(["Rollback steps"], await Find($"parent = {root}"));
        Assert.Equal(["Rollback steps"], await Find($"ancestor = {root}"));
        Assert.Equal(["Lunch menu"], await Find("space = ops AND NOT text ~ rollback"));
        Assert.Equal(["Release notes"], await Find("title = \"release NOTES\""));
        Assert.Equal(["Rollback steps"], await Find("text ~ \"colour~\" OR text ~ \"databa*\""));
        Assert.Equal(["Release notes"], await Find("text ~ \"\\\"rollback plan\\\"\""));
        Assert.Equal(["Lunch menu", "Release notes", "Rollback steps"], await Find("creator = cora AND created >= startOfDay(\"-1d\") ORDER BY title ASC"));
        Assert.Equal(["Rollback steps", "Release notes", "Lunch menu"], await Find("contributor = currentUser() OR type IN (page, blogpost) ORDER BY title DESC"));
        Assert.Empty(await Find("creator = currentUser()"));
        Assert.Equal(["Release notes"], Titles(await _rita.GetJsonAsync($"/rest/api/content/search?cql={Uri.EscapeDataString("space = currentSpace() AND title ~ release")}&cqlcontext={Uri.EscapeDataString("{\"spaceKey\":\"OPS\"}")}")));

        foreach (var bad in new[] { "", "NOT type = page", "bogus = 1", "title > 2", "created = yesterday", "label = runbook ORDER BY label", "type = page AND (title ~ x", "space = currentSpace()", "text = x", "title = and" })
            await (await _rita.GetAsync($"/rest/api/content/search?cql={Uri.EscapeDataString(bad)}", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _rita.GetAsync("/rest/api/search?cql=type%3Dpage&cqlcontext=%7Bbad", Ct)).ErrorAsync(HttpStatusCode.BadRequest);

        var results = await _rita.GetJsonAsync($"/rest/api/search?cql={Uri.EscapeDataString("text ~ rollback")}&limit=1");
        Assert.Equal((2, 1, "text ~ rollback"), (results.GetProperty("totalSize").GetInt32(), results.GetProperty("size").GetInt32(), results.GetProperty("cqlQuery").GetString()));
        var hit = results.GetProperty("results")[0];
        Assert.Contains("@@@hl@@@", hit.GetProperty("excerpt").GetString(), StringComparison.Ordinal);
        Assert.Equal("content", hit.GetProperty("entityType").GetString());
        Assert.Equal("Operations", hit.GetProperty("resultGlobalContainer").GetProperty("title").GetString());
        Assert.Equal("page", hit.GetProperty("content").GetProperty("type").GetString());
        Assert.True(results.GetProperty("_links").TryGetProperty("next", out _));
        var none = await _rita.GetJsonAsync($"/rest/api/search?cql={Uri.EscapeDataString("text ~ rollback")}&excerpt=none");
        Assert.Equal("", none.GetProperty("results")[0].GetProperty("excerpt").GetString());
        await (await _rita.GetAsync("/rest/api/search?cql=type%3Dpage&excerpt=all", Ct)).ErrorAsync(HttpStatusCode.BadRequest);

        // Archived spaces are left out unless asked for, and counted.
        // The break-glass account's browser session (password Basic is refused for it, Q-0020).
        (await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/rest/api/space/OPS/archive") { Headers = { { "X-Atlassian-Token", "no-check" } } }, Ct)).EnsureSuccessStatusCode();
        var archived = await _rita.GetJsonAsync($"/rest/api/search?cql={Uri.EscapeDataString("type = page")}");
        Assert.Equal((0, 3), (archived.GetProperty("totalSize").GetInt32(), archived.GetProperty("archivedResultCount").GetInt32()));
        Assert.Equal(3, (await _rita.GetJsonAsync($"/rest/api/search?cql={Uri.EscapeDataString("type = page")}&includeArchivedSpaces=true")).GetProperty("totalSize").GetInt32());
        Assert.Empty(await Find("type = page"));
        // Someone with no role in the space finds nothing.
        await UserAsync(_f, "out");
        Assert.Empty(Titles(await Basic(_f, "out").GetJsonAsync($"/rest/api/search?cql={Uri.EscapeDataString("type = page")}&includeArchivedSpaces=true")));
    }
}
