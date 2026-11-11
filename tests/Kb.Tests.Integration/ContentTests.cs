using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Core.Search;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>Move, recycle bin, search, versions, drafts, attachments, exports, home.</summary>
public sealed class ContentTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 2, 0, 0, 0, 3];
    private readonly KbAppFactory _f;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ContentTests()
    {
        var db = KbAppFactory.NewDbPath();
        _f = new(db, settings: new Dictionary<string, string> { ["Kb:Storage:Folder"] = db + ".files" });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string Doc(string text) => JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } } });

    private async Task<(HttpClient Admin, Guid Space)> SpaceAsync(string name = "Service Desk")
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var s = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        return (admin, s!.Id);
    }

    private static async Task<PageDto> CreateAsync(HttpClient c, Guid spaceId, string title, string kind = "Page", Guid? parentId = null, string? text = null, string[]? tags = null)
    {
        var r = await c.PostAsJsonAsync($"/api/v1/spaces/{spaceId}/pages", new { parentId, kind, title, tags, contentJson = kind == "Page" ? Doc(text ?? "") : null }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;
    }

    private async Task<HttpClient> UserAsync(string name, Guid spaceId, SpaceRole? role)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            var u = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await users.CreateAsync(u, "user-password-0123456789")).Succeeded);
            id = u.Id;
        }
        if (role is { } r)
        {
            var admin = await _f.SignedInBreakGlassAsync();
            await admin.PutAsJsonAsync($"/api/v1/spaces/{spaceId}/roles", new { principalType = "User", principalId = id.ToString(), role = r.ToString() }, Ct);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<List<TreeNodeDto>> TreeAsync(HttpClient c, Guid space) => (await c.GetFromJsonAsync<List<TreeNodeDto>>($"/api/v1/spaces/{space}/tree", Ct))!;
    private static async Task<SearchResponse> SearchAsync(HttpClient c, string query) => (await c.GetFromJsonAsync<SearchResponse>($"/api/v1/search?{query}", Ct))!;

    // ---- move ----

    [Fact]
    public async Task Move_IntoFolder_ThenFolderIntoItself_IsRefused()
    {
        var (admin, s) = await SpaceAsync();
        var folder = await CreateAsync(admin, s, "How-to", "Folder");
        var inner = await CreateAsync(admin, s, "Inner", "Folder", folder.Id);
        var page = await CreateAsync(admin, s, "Reset a password");

        var moved = await (await admin.PostAsJsonAsync($"/api/v1/pages/{page.Id}/move", new { parentId = inner.Id }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct);
        Assert.Equal(inner.Id, moved!.ParentId);
        Assert.Equal(["How-to", "Inner"], moved.Path.Select(p => p.Title));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/pages/{folder.Id}/move", new { parentId = inner.Id }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Move_ToAnotherSpace_TakesSubtree_AndImagesStayVisible()
    {
        var (admin, a) = await SpaceAsync("A");
        var (_, b) = await SpaceAsync("B");
        var upload = new ByteArrayContent(Png);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var img = await (await admin.PostAsync($"/api/v1/spaces/{a}/images", upload, Ct)).Content.ReadFromJsonAsync<ImageDto>(Ct);
        var folder = await CreateAsync(admin, a, "Guides", "Folder");
        var doc = JsonSerializer.Serialize(new { type = "doc", content = new object[] { new { type = "image", attrs = new { src = img!.Url } } } });
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{a}/pages", new { parentId = folder.Id, kind = "Page", title = "With picture", contentJson = doc }, Ct);
        var page = await r.Content.ReadFromJsonAsync<PageDto>(Ct);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/pages/{folder.Id}/move", new { spaceId = b, parentId = (Guid?)null }, Ct)).StatusCode);
        Assert.Empty(await TreeAsync(admin, a));
        Assert.Equal(2, (await TreeAsync(admin, b)).Count);

        var readerOfB = await UserAsync("bob", b, SpaceRole.Reader);
        Assert.Equal(b, (await readerOfB.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page!.Id}", Ct))!.SpaceId);
        Assert.Equal(HttpStatusCode.OK, (await readerOfB.GetAsync(img.Url, Ct)).StatusCode);
    }

    [Fact]
    public async Task MoveAndDelete_OfSomeoneElsesPage_NeedSupervisor()
    {
        var (admin, s) = await SpaceAsync();
        var page = await CreateAsync(admin, s, "Admin's page");
        var contributor = await UserAsync("carol", s, SpaceRole.Contributor);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.DeleteAsync($"/api/v1/pages/{page.Id}", Ct)).StatusCode);
        var own = await CreateAsync(contributor, s, "Carol's page");
        Assert.True(own.CanDelete);
        Assert.Equal(HttpStatusCode.NoContent, (await contributor.DeleteAsync($"/api/v1/pages/{own.Id}", Ct)).StatusCode);
    }

    // ---- recycle bin ----

    [Fact]
    public async Task Delete_HidesSubtree_Restore_BringsItBackInPlace()
    {
        var (admin, s) = await SpaceAsync();
        var folder = await CreateAsync(admin, s, "Servers", "Folder");
        var page = await CreateAsync(admin, s, "Patching", parentId: folder.Id, text: "monthly patching");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/pages/{folder.Id}", Ct)).StatusCode);
        Assert.Empty(await TreeAsync(admin, s));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/pages/{page.Id}", Ct)).StatusCode);
        Assert.Empty((await SearchAsync(admin, "q=patching")).Hits);

        var bin = await admin.GetFromJsonAsync<List<BinItemDto>>($"/api/v1/spaces/{s}/bin", Ct);
        var item = Assert.Single(bin!);
        Assert.Equal(("Servers", 2), (item.Title, item.Items));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/pages/{folder.Id}/restore", null, Ct)).StatusCode);
        Assert.Equal(2, (await TreeAsync(admin, s)).Count);
        Assert.Equal(folder.Id, (await admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page.Id}", Ct))!.ParentId);
        Assert.Single((await SearchAsync(admin, "q=patching")).Hits);
    }

    [Fact]
    public async Task Restore_PageWhoseFolderWasDeletedLater_RecreatesOnlyTheFolderChain()
    {
        var (admin, s) = await SpaceAsync();
        var folder = await CreateAsync(admin, s, "Network", "Folder");
        var wanted = await CreateAsync(admin, s, "VLANs", parentId: folder.Id);
        var other = await CreateAsync(admin, s, "Switches", parentId: folder.Id);
        await admin.DeleteAsync($"/api/v1/pages/{wanted.Id}", Ct);
        await admin.DeleteAsync($"/api/v1/pages/{folder.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/pages/{wanted.Id}/restore", null, Ct)).StatusCode);
        var tree = await TreeAsync(admin, s);
        Assert.Equal(["Network", "VLANs"], tree.Select(n => n.Title).Order());
        // The other page stays in the bin, as its own entry now.
        var bin = await admin.GetFromJsonAsync<List<BinItemDto>>($"/api/v1/spaces/{s}/bin", Ct);
        Assert.Equal(other.Id, Assert.Single(bin!).Id);
    }

    [Fact]
    public async Task Restore_WhenNameIsTaken_AddsRestoredSuffix()
    {
        var (admin, s) = await SpaceAsync();
        var first = await CreateAsync(admin, s, "Onboarding");
        await admin.DeleteAsync($"/api/v1/pages/{first.Id}", Ct);
        await CreateAsync(admin, s, "Onboarding");
        await admin.PostAsync($"/api/v1/pages/{first.Id}/restore", null, Ct);
        Assert.Contains(await TreeAsync(admin, s), n => n.Title == "Onboarding (restored)");
    }

    [Fact]
    public async Task EmptyBin_HidesFromSpace_AdminBinStillRestores()
    {
        var (admin, s) = await SpaceAsync();
        var page = await CreateAsync(admin, s, "Old page");
        var supervisor = await UserAsync("sue", s, SpaceRole.Supervisor);
        await admin.DeleteAsync($"/api/v1/pages/{page.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, (await supervisor.PostAsync($"/api/v1/spaces/{s}/bin/empty", null, Ct)).StatusCode);
        Assert.Empty((await supervisor.GetFromJsonAsync<List<BinItemDto>>($"/api/v1/spaces/{s}/bin", Ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await supervisor.PostAsync($"/api/v1/pages/{page.Id}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.GetAsync("/api/v1/admin/bin", Ct)).StatusCode);

        Assert.True(Assert.Single((await admin.GetFromJsonAsync<List<BinItemDto>>("/api/v1/admin/bin", Ct))!).Hidden);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/pages/{page.Id}/restore", null, Ct)).StatusCode);
        Assert.Single(await TreeAsync(admin, s));
    }

    [Fact]
    public async Task Bin_ForContributor_Returns403()
    {
        var (_, s) = await SpaceAsync();
        var contributor = await UserAsync("carol", s, SpaceRole.Contributor);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.GetAsync($"/api/v1/spaces/{s}/bin", Ct)).StatusCode);
    }

    // ---- search ----

    [Fact]
    public async Task Search_FindsByTitleBodyAndTag_TitleRanksFirst()
    {
        var (admin, s) = await SpaceAsync();
        await CreateAsync(admin, s, "Printer setup", text: "Install the driver first.");
        await CreateAsync(admin, s, "Office moves", text: "Remember to move the printer too.");
        await CreateAsync(admin, s, "Badge access", tags: ["printer"]);

        var r = await SearchAsync(admin, "q=printer");
        Assert.Equal(3, r.Total);
        Assert.Equal("Printer setup", r.Hits[0].Title);
        Assert.Equal("/p/" + r.Hits[0].Id, r.Hits[0].Url);
        Assert.Equal("Service Desk", r.Hits[0].SpaceName);
        Assert.Contains("printer", r.Hits[1].Snippet + r.Hits[2].Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_ToleratesTypos_AndUsesSynonyms()
    {
        var (admin, s) = await SpaceAsync();
        await CreateAsync(admin, s, "Reset a password", text: "Use the self-service portal.");

        Assert.Single((await SearchAsync(admin, "q=pasword")).Hits);       // one letter missing
        Assert.Single((await SearchAsync(admin, "q=passwrod")).Hits);      // two letters swapped
        Assert.Single((await SearchAsync(admin, "q=pw")).Hits);            // synonym
        Assert.Single((await SearchAsync(admin, "q=pass")).Hits);          // prefix
        Assert.Empty((await SearchAsync(admin, "q=firewall")).Hits);
    }

    [Fact]
    public async Task Search_OnlyReturnsSpacesTheUserCanRead()
    {
        var (admin, open) = await SpaceAsync("Open");
        var (_, closed) = await SpaceAsync("Closed");
        await CreateAsync(admin, open, "Laptop imaging");
        await CreateAsync(admin, closed, "Laptop disposal");
        var reader = await UserAsync("rita", open, SpaceRole.Reader);

        var r = await SearchAsync(reader, "q=laptop");
        Assert.Equal("Laptop imaging", Assert.Single(r.Hits).Title);
        Assert.Empty((await SearchAsync(reader, $"q=laptop&space={closed}")).Hits);
    }

    [Fact]
    public async Task Search_ForAgents_ReturnsMarkdownPathAndVersion()
    {
        var (admin, s) = await SpaceAsync();
        var folder = await CreateAsync(admin, s, "How-to", "Folder");
        var page = await CreateAsync(admin, s, "Map a drive", parentId: folder.Id, text: "Open File Explorer.", tags: ["windows"]);

        var hit = Assert.Single((await SearchAsync(admin, "q=drive&include=markdown")).Hits, h => h.Kind == "Page");
        Assert.Equal(["How-to"], hit.Path);
        Assert.Equal("1.0", hit.Version);
        Assert.Equal(["windows"], hit.Tags);
        Assert.Equal("Open File Explorer.", hit.Text);

        Assert.Equal(page.Id, Assert.Single((await SearchAsync(admin, "tag=windows")).Hits).Id);
        var md = await admin.GetFromJsonAsync<PageMarkdownDto>($"/api/v1/pages/{page.Id}/markdown", Ct);
        Assert.Equal("Open File Explorer.", md!.Markdown);
    }

    [Fact]
    public async Task Synonyms_AreAdminEditable()
    {
        var (admin, s) = await SpaceAsync();
        await CreateAsync(admin, s, "Conference room booking");
        Assert.Empty((await SearchAsync(admin, "q=boardroom")).Hits);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/search/synonyms", new { text = "boardroom, conference room" }, Ct)).StatusCode);
        Assert.Single((await SearchAsync(admin, "q=boardroom")).Hits);
    }

    // ---- versions ----

    [Fact]
    public async Task Diff_ShowsAddedAndRemovedLines_RestoreMakesANewVersion()
    {
        var (admin, s) = await SpaceAsync();
        var page = await CreateAsync(admin, s, "Runbook", text: "Step one");
        await admin.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { title = "Runbook", contentJson = Doc("Step 1"), baseVersion = "1.0" }, Ct);

        var diff = await admin.GetFromJsonAsync<DiffDto>($"/api/v1/pages/{page.Id}/diff?from=1.0&to=1.1", Json, Ct);
        Assert.Contains(diff!.Lines, l => l.Op == DiffOp.Del && l.Text == "Step one");
        Assert.Contains(diff.Lines, l => l.Op == DiffOp.Add && l.Text == "Step 1");
        Assert.Contains(diff.Lines, l => l.Op == DiffOp.Same && l.Text == "# Runbook");

        var old = await admin.GetFromJsonAsync<VersionContentDto>($"/api/v1/pages/{page.Id}/versions/1.0", Ct);
        Assert.Contains("Step one", old!.ContentJson, StringComparison.Ordinal);

        var restored = await (await admin.PostAsync($"/api/v1/pages/{page.Id}/versions/1.0/restore", null, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct);
        Assert.Equal("1.2", restored!.Version);
        Assert.Contains("Step one", restored.ContentJson, StringComparison.Ordinal);
        Assert.Equal(3, (await admin.GetFromJsonAsync<List<VersionDto>>($"/api/v1/pages/{page.Id}/versions", Ct))!.Count);
        Assert.Equal("Restored from 1.0", restored.History[0].Note);
    }

    // ---- drafts ----

    [Fact]
    public async Task Draft_IsKeptPerPage_SecondEditorIsTold_SaveClearsIt()
    {
        var (admin, s) = await SpaceAsync();
        var page = await CreateAsync(admin, s, "Policy", text: "v1");
        var other = await UserAsync("dana", s, SpaceRole.Contributor);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/pages/{page.Id}/draft", new { title = "Policy", contentJson = Doc("unsaved"), baseVersion = "1.0" }, Ct)).StatusCode);
        var mine = await admin.GetFromJsonAsync<DraftDto>($"/api/v1/pages/{page.Id}/draft", Ct);
        Assert.Contains("unsaved", mine!.ContentJson, StringComparison.Ordinal);

        var seen = await other.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page.Id}", Ct);
        Assert.False(seen!.Draft!.Mine);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PutAsJsonAsync($"/api/v1/pages/{page.Id}/draft", new { title = "Policy", contentJson = Doc("theirs"), baseVersion = "1.0" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/pages/{page.Id}/draft", Ct)).StatusCode);

        var saved = await (await admin.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { title = "Policy", contentJson = Doc("v2"), baseVersion = "1.0" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct);
        Assert.Null(saved!.Draft);
    }

    // ---- attachments ----

    [Fact]
    public async Task Attachment_UploadListDownloadRemove()
    {
        var (admin, s) = await SpaceAsync();
        var page = await CreateAsync(admin, s, "Installer");
        var body = new ByteArrayContent("#!/bin/sh\necho hi"u8.ToArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", Uri.EscapeDataString("set up.sh"));
        var a = await (await admin.PostAsync($"/api/v1/pages/{page.Id}/attachments", body, Ct)).Content.ReadFromJsonAsync<AttachmentDto>(Ct);
        Assert.Equal("set up.sh", a!.Name);

        var reader = await UserAsync("rita", s, SpaceRole.Reader);
        Assert.Equal(a.Id, Assert.Single((await reader.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page.Id}", Ct))!.Attachments).Id);
        var download = await reader.GetAsync(a.Url, Ct);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(a.Url, Ct)).StatusCode);

        var outsider = await UserAsync("oscar", s, null);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(a.Url, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(a.Url, Ct)).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page.Id}", Ct))!.Attachments);
    }

    // ---- exports ----

    [Fact]
    public async Task Export_PdfWordTableAndCode()
    {
        var (admin, s) = await SpaceAsync();
        var doc = JsonSerializer.Serialize(new
        {
            type = "doc",
            content = new object[]
            {
                new { type = "heading", attrs = new { level = 2 }, content = new[] { new { type = "text", text = "Disk cleanup" } } },
                new { type = "paragraph", content = new object[] { new { type = "text", text = "Run this ", marks = new[] { new { type = "bold" } } }, new { type = "text", text = "weekly." } } },
                new { type = "codeBlock", attrs = new { language = "powershell" }, content = new[] { new { type = "text", text = "Get-ChildItem C:\\Temp | Remove-Item" } } },
                new { type = "table", content = new object[]
                {
                    new { type = "tableRow", content = new object[] { Cell("tableHeader", "Server"), Cell("tableHeader", "Free GB") } },
                    new { type = "tableRow", content = new object[] { Cell("tableCell", "FS01, primary"), Cell("tableCell", "120") } },
                } },
            },
        });
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{s}/pages", new { kind = "Page", title = "Disk: cleanup/guide", contentJson = doc }, Ct);
        var page = (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;

        var pdf = await admin.GetAsync($"/api/v1/pages/{page.Id}/export?format=pdf", Ct);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync(Ct))[..4]), StringComparison.Ordinal);
        Assert.Equal("Disk-cleanupguide.pdf", pdf.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var docx = await (await admin.GetAsync($"/api/v1/pages/{page.Id}/export?format=docx", Ct)).Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal("PK", Encoding.ASCII.GetString(docx[..2]));

        var csv = await (await admin.GetAsync($"/api/v1/pages/{page.Id}/export/table/1?format=csv", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Equal("Server,Free GB\r\n\"FS01, primary\",120\r\n", csv.TrimStart('\uFEFF'));
        var xlsx = await (await admin.GetAsync($"/api/v1/pages/{page.Id}/export/table/1?format=xlsx", Ct)).Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal("PK", Encoding.ASCII.GetString(xlsx[..2]));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/pages/{page.Id}/export/table/2", Ct)).StatusCode);

        var code = await admin.GetAsync($"/api/v1/pages/{page.Id}/export/code/1", Ct);
        Assert.Equal("Disk-cleanup.ps1", code.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("Get-ChildItem C:\\Temp | Remove-Item", await code.Content.ReadAsStringAsync(Ct));

        var outsider = await UserAsync("oscar", s, null);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/pages/{page.Id}/export?format=pdf", Ct)).StatusCode);

        static object Cell(string type, string text) => new { type, content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } } };
    }

    // ---- home ----

    [Fact]
    public async Task Announcements_NeedThePermission_AndShowOnHome()
    {
        var (admin, s) = await SpaceAsync();
        await CreateAsync(admin, s, "Newest page");
        var user = await UserAsync("ann", s, SpaceRole.Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/announcements", new { title = "Outage" }, Ct)).StatusCode);

        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users", Ct);
        var ann = users!.Single(u => u.UserName == "ann");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/admin/users/{ann.Id}/announcer", new { enabled = true }, Ct)).StatusCode);
        Assert.True((await user.GetFromJsonAsync<MeResponse>("/api/v1/me", Ct))!.CanAnnounce);
        Assert.Equal(HttpStatusCode.Created, (await user.PostAsJsonAsync("/api/v1/announcements", new { title = "Outage", body = "Email is down." }, Ct)).StatusCode);

        var home = await user.GetFromJsonAsync<HomeDto>("/api/v1/home", Ct);
        var a = Assert.Single(home!.Announcements);
        Assert.Equal(("Outage", "ann"), (a.Title, a.By));
        Assert.Equal("Newest page", Assert.Single(home.Recent).Title);

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/announcements/{a.Id}", Ct)).StatusCode);
        Assert.Empty((await user.GetFromJsonAsync<HomeDto>("/api/v1/home", Ct))!.Announcements);
    }

    // ---- image cleanup ----

    [Fact]
    public async Task ImageCleanup_RemovesOnlyOldUploadsNoPageUses()
    {
        var (admin, s) = await SpaceAsync();
        async Task<ImageDto> UploadAsync()
        {
            var c = new ByteArrayContent(Png);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return (await (await admin.PostAsync($"/api/v1/spaces/{s}/images", c, Ct)).Content.ReadFromJsonAsync<ImageDto>(Ct))!;
        }
        var used = await UploadAsync();
        var unused = await UploadAsync();
        var doc = JsonSerializer.Serialize(new { type = "doc", content = new object[] { new { type = "image", attrs = new { src = used.Url } } } });
        await admin.PostAsJsonAsync($"/api/v1/spaces/{s}/pages", new { kind = "Page", title = "Pic", contentJson = doc }, Ct);

        await using var scope = _f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IFileStore>();
        Assert.Equal(0, await ImageEndpoints.CleanupAsync(db, store, DateTimeOffset.UtcNow.AddDays(1), Ct));  // too new to judge
        Assert.Equal(1, await ImageEndpoints.CleanupAsync(db, store, DateTimeOffset.UtcNow.AddDays(8), Ct));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(used.Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(unused.Url, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void NetGuard_AllowsOnlyPublicAddresses(string ip, bool expected) => Assert.Equal(expected, NetGuard.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task ImageImport_RefusesInternalAndNonHttpAddresses()
    {
        var (admin, s) = await SpaceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/spaces/{s}/images/import", new { url = "http://127.0.0.1/logo.png" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/spaces/{s}/images/import", new { url = "file:///c:/windows/win.ini" }, Ct)).StatusCode);
    }

    [Fact]
    public void SearchText_Basics()
    {
        Assert.Equal(["reset", "pw", "win11"], SearchText.Tokenize("Reset: PW (Win11)!"));
        Assert.Equal(1, SearchText.Distance("pasword", "password", 2));
        Assert.Equal(3, SearchText.Distance("printer", "firewall", 2));
        Assert.Equal(["pw", "passcode"], SearchText.ParseSynonyms("password, pw, passcode")["password"]);
    }

    [Fact]
    public void DocText_MarkdownCoversBlocks()
    {
        var json = """
            {"type":"doc","content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Steps"}]},
              {"type":"orderedList","content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Open ","marks":[{"type":"bold"}]},{"type":"text","text":"Settings"}]}]},
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Docs","marks":[{"type":"link","attrs":{"href":"https://example.com"}}]}]}]}]},
              {"type":"taskList","content":[{"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"Done"}]}]}]},
              {"type":"codeBlock","attrs":{"language":"bash"},"content":[{"type":"text","text":"ls -la"}]}
            ]}
            """;
        Assert.Equal(["## Steps", "", "1. **Open **Settings", "2. [Docs](https://example.com)", "", "- [x] Done", "", "```bash", "ls -la", "```"], DocText.MarkdownLines(json));
        Assert.Equal("Steps\nOpen Settings Docs\nDone\nls -la", DocText.Plain(json));
    }

    [Fact]
    public void LineDiff_KeepsCommonLines()
    {
        var d = LineDiff.Diff(["a", "b", "c"], ["a", "x", "c", "d"]);
        Assert.Equal([(DiffOp.Same, "a"), (DiffOp.Del, "b"), (DiffOp.Add, "x"), (DiffOp.Same, "c"), (DiffOp.Add, "d")], d.Select(l => (l.Op, l.Text)));
    }
}
