using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Web;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The export routes go through the action bus: each answers with its action's name, a plugin can refuse and shape an export,
/// and the files, headers and audit entries are what they were.
/// </summary>
public sealed class ExportPipelineTests : IAsyncLifetime
{
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Csv = "Server,Free GB\r\n\"FS01, primary\",120\r\n";
    private const string Code = "Get-ChildItem C:\\Temp | Remove-Item";
    // The start of a PNG that is 2 by 3 pixels: all an export reads of an image before it puts the file into the document.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 2, 0, 0, 0, 3];
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space, _page;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        _page = await CreatedAsync("Page", "Disk: cleanup/guide",
            new { type = "heading", attrs = new { level = 2 }, content = new[] { new { type = "text", text = "Disk cleanup" } } },
            new { type = "codeBlock", attrs = new { language = "powershell" }, content = new[] { new { type = "text", text = Code } } },
            new { type = "table", content = new object[]
            {
                new { type = "tableRow", content = new object[] { Cell("tableHeader", "Server"), Cell("tableHeader", "Free GB") } },
                new { type = "tableRow", content = new object[] { Cell("tableCell", "FS01, primary"), Cell("tableCell", "120") } },
            } });

        static object Cell(string type, string text) => new { type, content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } } };
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> CreatedAsync(string kind, string title, params object[] content)
    {
        var contentJson = JsonSerializer.Serialize(new { type = "doc", content });
        var r = await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind, title, contentJson }, Ct);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
    }

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private Task<HttpResponseMessage> ExportAsync(string path = "", Guid? page = null) => _admin.GetAsync($"/api/v1/pages/{page ?? _page}/export{path}", Ct);
    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;
    private static async Task<string?> FormatErrorAsync(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("format")[0].GetString();

    /// <summary>Every header but the two the app adds to each answer. The length is checked against the body, since a PDF or a Word file is not the same size twice.</summary>
    private static async Task<IEnumerable<string>> HeadersAsync(HttpResponseMessage r)
    {
        Assert.Equal((await r.Content.ReadAsByteArrayAsync(Ct)).LongLength, r.Content.Headers.ContentLength);
        return r.Headers.Concat(r.Content.Headers).Where(h => h.Key is not ("X-Kb-Action" or "X-Kb-Version" or "Content-Length"))
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").Order(StringComparer.Ordinal);
    }

    private static string[] Download(string type, string name) =>
    [
        $"Content-Disposition: attachment; filename={name}; filename*=UTF-8''{name}",
        // The app's own policy, as on every answer: an export sets no header of its own.
        "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'",
        $"Content-Type: {type}",
        "Cross-Origin-Resource-Policy: same-origin",
        "Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()",
        "Referrer-Policy: same-origin",
        "X-Content-Type-Options: nosniff",
        "X-Frame-Options: DENY",
    ];

    [Fact]
    public async Task EveryExportRoute_AnswersWithItsActionName()
    {
        foreach (var (path, action) in new[]
        {
            ("", "page.export"), ("?format=pdf", "page.export"), ("?format=DOCX", "page.export"),
            ("/table/1", "page.export.table"), ("/table/1?format=xlsx", "page.export.table"), ("/code/1", "page.export.code"),
        })
        {
            var r = await ExportAsync(path);
            Assert.Equal((path, HttpStatusCode.OK, action), (path, r.StatusCode, Action(r)));
        }

        // A refusal by the core names the action too. A folder is not a page; a table or a code block that is not there is a 404 before the format is looked at.
        var folder = await CreatedAsync("Folder", "Guides");
        foreach (var (page, path, action) in new[]
        {
            (Guid.NewGuid(), "", "page.export"), (folder, "", "page.export"),
            (Guid.NewGuid(), "/table/1", "page.export.table"), (folder, "/table/1", "page.export.table"),
            (_page, "/table/0", "page.export.table"), (_page, "/table/2?format=md", "page.export.table"),
            (Guid.NewGuid(), "/code/1", "page.export.code"), (folder, "/code/1", "page.export.code"),
            (_page, "/code/0", "page.export.code"), (_page, "/code/2", "page.export.code"),
        })
        {
            var r = await ExportAsync(path, page);
            Assert.Equal((path, HttpStatusCode.NotFound, action), (path, r.StatusCode, Action(r)));
        }

        var bad = await ExportAsync("?format=md");
        Assert.Equal((HttpStatusCode.BadRequest, "page.export", "pdf or docx."), (bad.StatusCode, Action(bad), await FormatErrorAsync(bad)));
        var badTable = await ExportAsync("/table/1?format=pdf");
        Assert.Equal((HttpStatusCode.BadRequest, "page.export.table", "csv or xlsx."), (badTable.StatusCode, Action(badTable), await FormatErrorAsync(badTable)));
    }

    /// <summary>The files and their headers, all of them, as they were before the routes sent an action. Only X-Kb-Action is new.</summary>
    [Fact]
    public async Task Exports_FilesAndHeadersAreWhatTheyWere()
    {
        var pdf = await ExportAsync();
        Assert.Equal(Download("application/pdf", "Disk-cleanupguide.pdf"), await HeadersAsync(pdf));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync(Ct))[..4]), StringComparison.Ordinal);

        var docx = await ExportAsync("?format=docx");
        Assert.Equal(Download(Docx, "Disk-cleanupguide.docx"), await HeadersAsync(docx));
        Assert.Equal("PK", Encoding.ASCII.GetString((await docx.Content.ReadAsByteArrayAsync(Ct))[..2]));

        var csv = await ExportAsync("/table/1");
        Assert.Equal(Download("text/csv", "Disk-cleanupguide-table-1.csv"), await HeadersAsync(csv));
        Assert.Equal(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv)), await csv.Content.ReadAsByteArrayAsync(Ct));

        var xlsx = await ExportAsync("/table/1?format=xlsx");
        Assert.Equal(Download(Xlsx, "Disk-cleanupguide-table-1.xlsx"), await HeadersAsync(xlsx));
        Assert.Equal("PK", Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync(Ct))[..2]));

        // A code block is named after the heading above it, with the extension of its language.
        var code = await ExportAsync("/code/1");
        Assert.Equal(Download("application/octet-stream", "Disk-cleanup.ps1"), await HeadersAsync(code));
        Assert.Equal(Encoding.UTF8.GetBytes(Code), await code.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Export_PutsThePagesStoredImagesIntoTheDocument()
    {
        var body = new ByteArrayContent(Png);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var image = (await (await _admin.PostAsync($"/api/v1/spaces/{_space}/images", body, Ct)).Content.ReadFromJsonAsync<ImageDto>(Ct))!;
        var page = await CreatedAsync("Page", "With a picture",
            new { type = "image", attrs = new { src = image.Url } },
            new { type = "image", attrs = new { src = $"/api/v1/images/{Guid.NewGuid()}" } }); // not stored: left out

        var docx = await ExportAsync("?format=docx", page);

        Assert.Equal(HttpStatusCode.OK, docx.StatusCode);
        using var zip = new ZipArchive(await docx.Content.ReadAsStreamAsync(Ct));
        var media = Assert.Single(zip.Entries, e => e.FullName.Contains("media/", StringComparison.Ordinal));
        using var stored = new MemoryStream();
        await using (var s = await media.OpenAsync(Ct)) await s.CopyToAsync(stored, Ct);
        Assert.Equal(Png, stored.ToArray());
    }

    [Fact]
    public async Task Exports_AreAudited_AsTheCaller_AndOnlyWhenTheyAnswerAFile()
    {
        foreach (var path in new[] { "", "?format=docx", "/table/1", "/table/1?format=xlsx", "/code/1", "?format=md", "/table/1?format=pdf", "/table/2", "/code/2" })
            await ExportAsync(path);

        await _f.WithDbAsync(async db =>
        {
            var me = (await db.Users.SingleAsync(u => u.IsBreakGlass, Ct)).Id;
            var entries = await db.AuditEntries.Where(e => e.Action == AuditActions.PageExport).ToListAsync(Ct);
            Assert.All(entries, e => Assert.Equal((me, "page", _page.ToString(), _space), (e.ActorId, e.TargetType, e.TargetId, e.SpaceId)));
            Assert.Equal(new[] { "code", "docx", "pdf", "table-csv", "table-xlsx" }.Select(format => $$"""{"format":"{{format}}","version":"1.0"}"""),
                entries.Select(e => e.DataJson).Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task Export_RefusedByHook_Is422_NothingIsAnsweredOrAudited()
    {
        await SetAsync("Refuse", "page.export");

        var r = await ExportAsync();

        Assert.Equal(((HttpStatusCode)422, null), (r.StatusCode, r.Content.Headers.ContentDisposition));
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(e => e.Action == AuditActions.PageExport, Ct)));
        // The table and the code block are actions of their own.
        Assert.Equal(HttpStatusCode.OK, (await ExportAsync("/table/1")).StatusCode);
    }

    [Fact]
    public async Task Export_HookChangesTheFormat_TheChangedFormatIsExportedAndAudited()
    {
        await SetAsync("Rewrite", "page.export:Format=docx");

        var r = await ExportAsync("?format=pdf");

        Assert.Equal(Download(Docx, "Disk-cleanupguide.docx"), await HeadersAsync(r));
        await _f.WithDbAsync(async db =>
            Assert.Equal("""{"format":"docx","version":"1.0"}""", (await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.PageExport, Ct)).DataJson));
    }

    [Fact]
    public async Task Export_FilterShapesTheResult()
    {
        await SetAsync("Blank", "page.export.code:FileName");

        var r = await ExportAsync("/code/1");

        // The filter took the name from the answer: the file is no longer a download, and its content is the same.
        Assert.Equal((HttpStatusCode.OK, null), (r.StatusCode, r.Content.Headers.ContentDisposition));
        Assert.Equal(Code, await r.Content.ReadAsStringAsync(Ct));
    }
}
