using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Core.Content;
using Kb.Tests.Integration.DataCenter;
using Kb.Web;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The Low findings of the code review of 2026-11-17 about what a page may hold and how much: L8 (link and image addresses), L9 (style
/// and opaque markup on the compatible API), L10 (formula cells in a CSV export), L12 (text without a limit), L13 (a total per space).
/// </summary>
public sealed class ContentLimitsTests
{
    private static string Doc(string href, string src) => JsonSerializer.Serialize(new
    {
        type = "doc",
        content = new object[]
        {
            new { type = "paragraph", content = new[] { new { type = "text", text = "click", marks = new[] { new { type = "link", attrs = new { href } } } } } },
            new { type = "image", attrs = new { src, alt = "x" } },
        },
    });

    private static async Task<(KbAppFactory F, HttpClient Admin, Guid Space)> SiteAsync(IDictionary<string, string>? settings = null)
    {
        var f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: settings);
        var admin = await f.SignedInBreakGlassAsync();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return (f, admin, space);
    }

    // ---- L8 ----

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData(" JaVa\tScRiPt:alert(1)")]
    [InlineData("vbscript:x")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public async Task ASavedPage_HoldsNoAddressThatRuns(string bad)
    {
        var (f, admin, space) = await SiteAsync();
        await using var _ = f;
        var created = await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Links", contentJson = Doc(bad, bad) }, Ct)).JsonAsync(HttpStatusCode.Created);
        Assert.DoesNotContain("script", created.GetProperty("contentJson").GetString()!, StringComparison.OrdinalIgnoreCase);
        // A save is cleaned the same way.
        var saved = await (await admin.PutAsJsonAsync($"/api/v1/pages/{created.GetProperty("id").GetGuid()}", new { title = "Links", contentJson = Doc(bad, bad), baseVersion = created.GetProperty("version").GetString() }, Ct)).JsonAsync(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(saved.GetProperty("contentJson").GetString()!).RootElement.GetProperty("content");
        Assert.Equal("", doc[0].GetProperty("content")[0].GetProperty("marks")[0].GetProperty("attrs").GetProperty("href").GetString());
        Assert.Equal("", doc[1].GetProperty("attrs").GetProperty("src").GetString());
    }

    [Fact]
    public void OrdinaryAddresses_AreKept_AndOldPagesWriteNoBadLinkAsMarkdown()
    {
        foreach (var (href, src) in new[] { ("https://example.org/a?b=1", "/api/v1/images/0b0e7c6e-0000-4000-8000-000000000001"), ("/p/123", "http://example.org/x.png"), ("#heading", "data:image/png;base64,AAAA"), ("mailto:ops@example.org", "relative.png") })
        {
            var doc = Doc(href, src);
            Assert.Same(doc, DocLinks.Clean(doc));
        }
        // A page stored before this rule is not rewritten; its Markdown gives the link's words without the address.
        var md = DocText.Markdown(Doc("javascript:alert(1)", "/x.png"));
        Assert.Contains("click", md, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", md, StringComparison.OrdinalIgnoreCase);
    }

    // ---- L9 ----

    [Fact]
    public async Task CompatibleView_KeepsOnlyPlacingAndColouringStyles_AndSkipsOpaqueTextThatIsNotMarkup()
    {
        var (f, admin, space) = await SiteAsync();
        await using var _ = f;
        await UserAsync(f, "cora");
        var coraId = (await UserIdAsync(f, "cora"))!.Value;
        (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = coraId.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var cora = Basic(f, "cora");
        var view = (await (await cora.PostJsonAsync("/rest/api/contentbody/convert/view", new
        {
            representation = "storage",
            value = "<p style=\"text-align: center; position: fixed; background-image: url(https://tracker.example/t.png); color: #c00; width: 50%\">mid</p>",
        })).JsonAsync(HttpStatusCode.OK)).GetProperty("value").GetString()!;
        Assert.Contains("text-align: center;", view, StringComparison.Ordinal);
        Assert.Contains("color: #c00;", view, StringComparison.Ordinal);
        Assert.DoesNotContain("position", view, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.example", view, StringComparison.Ordinal);

        // A save through aerie's own API can put any text into an opaque node. Text that is not markup is left out of the storage
        // body, and the page can still be read through the compatible API.
        var json = JsonSerializer.Serialize(new { type = "doc", content = new object[] {
            new { type = "paragraph", content = new[] { new { type = "text", text = "before" } } },
            new { type = "dcStorage", attrs = new { xml = "<p>never closed <script>" } } } });
        var page = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Opaque", contentJson = json }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var found = await cora.GetJsonAsync("/rest/api/content?spaceKey=OPS&title=Opaque&expand=body.storage,body.view");
        var body = found.GetProperty("results")[0].GetProperty("body");
        Assert.Equal("<p>before</p>", body.GetProperty("storage").GetProperty("value").GetString());
        Assert.DoesNotContain("script", body.GetProperty("view").GetProperty("value").GetString()!, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, page);
    }

    // ---- L10 ----

    [Fact]
    public void CsvExport_TreatsACellThatStartsWithATabOrReturnAsText()
    {
        var csv = Encoding.UTF8.GetString(Exports.Csv([["\t=HYPERLINK(\"https://evil.example\")", "\r=1+1", "=2+2", "-5", "plain"]]));
        Assert.Contains("'\t=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("'\r=1+1", csv, StringComparison.Ordinal);
        Assert.Contains("'=2+2", csv, StringComparison.Ordinal);
        Assert.Contains(",-5,plain", csv, StringComparison.Ordinal); // a number stays a number
    }

    // ---- L12 ----

    [Fact]
    public async Task Names_Descriptions_Notes_AndChunkedUploads_HaveALimit()
    {
        var (f, admin, space) = await SiteAsync();
        await using var _ = f;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = new string('n', 201) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Long", description = new string('d', 2001) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/v1/spaces/{space}", new { description = new string('d', 2001) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/spaces/{space}", new { description = new string('d', 2000) }, Ct)).StatusCode);

        const string doc = """{"type":"doc","content":[]}""";
        var page = await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Notes", contentJson = doc }, Ct)).JsonAsync(HttpStatusCode.Created);
        var (id, version) = (page.GetProperty("id").GetGuid(), page.GetProperty("version").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/pages/{id}", new { title = "Notes", contentJson = doc, baseVersion = version, note = new string('x', 501) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/pages/{id}", new { title = "Notes", contentJson = doc, baseVersion = version, note = new string('x', 500) }, Ct)).StatusCode);

        // An image sent in chunks says no length up front: it is refused once it passes the limit, not after all of it was taken in.
        var body = new StreamContent(new MemoryStream(new byte[ImageEndpoints.MaxImageBytes + 200_000]));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", "big.png");
        var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/spaces/{space}/images") { Content = body };
        upload.Headers.TransferEncodingChunked = true;
        Assert.False((await admin.SendAsync(upload, Ct)).IsSuccessStatusCode);
    }

    // ---- L13 ----

    [Fact]
    public async Task ASpace_TakesNoMoreAttachmentsThanItsLimit_WhenOneIsSet()
    {
        var (f, admin, space) = await SiteAsync(new Dictionary<string, string> { ["Kb:Storage:SpaceQuotaMb"] = "1" });
        await using var _ = f;
        var page = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Files", contentJson = """{"type":"doc","content":[]}""" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        async Task<HttpStatusCode> AttachAsync(string name)
        {
            var body = new ByteArrayContent(new byte[600_000]);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Headers.Add("X-File-Name", name);
            return (await admin.PostAsync($"/api/v1/pages/{page}/attachments", body, Ct)).StatusCode;
        }
        Assert.True((int)await AttachAsync("one.bin") is >= 200 and < 300);
        Assert.Equal(HttpStatusCode.BadRequest, await AttachAsync("two.bin")); // 1.2 MB would pass the 1 MB the space may hold
    }
}
