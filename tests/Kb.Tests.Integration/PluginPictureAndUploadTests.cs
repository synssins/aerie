using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using Kb.Core.Content;
using Kb.Plugins;
using Kb.Plugins.Actions;
using Kb.Web;
using Kb.Web.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Contract 0.4.0: a renderer's own picture in Word and PDF exports (PNG only, within limits, else its alt text), and files a plugin
/// makes, sent through the bus with the limits and rights of an upload.
/// </summary>
public sealed class PluginPictureAndUploadTests : IAsyncLifetime
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><image href="https://example.com/x.png"/></svg>""");
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static byte[] PngOfSize(int width, int height)
    {
        var d = (byte[])Png.Clone();
        d[16] = (byte)(width >> 24); d[17] = (byte)(width >> 16); d[18] = (byte)(width >> 8); d[19] = (byte)width;
        d[20] = (byte)(height >> 24); d[21] = (byte)(height >> 16); d[22] = (byte)(height >> 8); d[23] = (byte)height;
        return d;
    }

    [Fact]
    public void APicture_IsDrawn_OnlyWhenItIsAPngWithinTheLimits()
    {
        Assert.NotNull(Exports.Picture(new DocPicture("image/png", Png, "chart")));
        Assert.Null(Exports.Picture(new DocPicture("image/svg+xml", Svg, "chart")));          // SVG from a plugin is never drawn
        Assert.Null(Exports.Picture(new DocPicture("image/png", Svg, "chart")));              // nor anything that only says it is a PNG
        Assert.Null(Exports.Picture(new DocPicture("image/png", PngOfSize(5000, 10), "chart")));
        Assert.Null(Exports.Picture(new DocPicture("image/png", [.. Png, .. new byte[Exports.MaxPictureBytes]], "chart")));
    }

    [Fact]
    public void WordAndPdf_DrawTheRenderersPicture_AndShowTheAltTextOfOneTheyCannot()
    {
        // The picture comes through the renderer guard like any block a renderer gives.
        var renderers = new DocRenderers([new Registered<NodeRenderer>("kb.test.chart", new NodeRenderer
        {
            Type = "chart",
            Blocks = n => [new DocPicture(n.GetProperty("attrs").GetProperty("svg").GetBoolean() ? "image/svg+xml" : "image/png", n.GetProperty("attrs").GetProperty("svg").GetBoolean() ? Svg : Png, "Org chart", 300)],
        })]);
        var blocks = new[] { """{"type":"chart","attrs":{"svg":false}}""", """{"type":"chart","attrs":{"svg":true}}""" }
            .SelectMany(j => renderers.Blocks(JsonDocument.Parse(j).RootElement)!).ToList();
        Assert.All(blocks, b => Assert.IsType<DocPicture>(b));

        var docx = Exports.Docx("Charts", blocks, new Dictionary<Guid, ExportImage>());
        using (var doc = WordprocessingDocument.Open(new MemoryStream(docx), false))
        {
            Assert.Single(doc.MainDocumentPart!.ImageParts);
            Assert.Contains("[image: Org chart]", doc.MainDocumentPart.Document!.InnerText, StringComparison.Ordinal);
            Assert.Contains(doc.MainDocumentPart.Document.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>(), p => p.Description == "Org chart");
        }
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Exports.Pdf("Charts", blocks, new Dictionary<Guid, ExportImage>()));
        var page = pdf.GetPage(1);
        Assert.Single(page.GetImages());
        Assert.Contains("[image:Orgchart]", page.Text.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal); // the PDF text reads without its spaces
    }

    [Fact]
    public async Task AFileAPluginMakes_IsAddedThroughTheBus_WithTheLimitsAndRightsOfAnUpload()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "ops", name = "Ops" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "P", contentJson = """{"type":"doc","content":[]}""" }, Ct))
            .Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
        Guid me = default;
        await _f.WithDbAsync(async db => me = (await db.Users.SingleAsync(u => u.UserName == "breakglass", Ct)).Id);

        async Task<Outcome<T>> SendAsync<T>(Actor actor, IAction<T> action)
        {
            await using var scope = _f.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActorAccessor>().Use(actor);
            return await scope.ServiceProvider.GetRequiredService<IActionBus>().SendAsync(action, Ct);
        }

        var person = Actor.User(me);
        var attached = await SendAsync(person, new AddAttachment { PageId = page, File = UploadContent.FromBytes("chart.png", Png) });
        Assert.True(attached.Ok);
        Assert.Equal(Png.Length, attached.Value!.Size);
        var streamed = await SendAsync(person, new AddAttachment { PageId = page, File = UploadContent.FromStream("notes.txt", _ => Task.FromResult<Stream>(new MemoryStream("hello"u8.ToArray()))) });
        Assert.True(streamed.Ok);
        Assert.Equal(5, streamed.Value!.Size);

        Assert.True((await SendAsync(person, new AddImage { SpaceId = space, File = UploadContent.FromBytes("chart.png", Png) })).Ok);
        Assert.Equal(400, (await SendAsync(person, new AddImage { SpaceId = space, File = UploadContent.FromBytes("chart.svg", Svg) })).Problem!.Status); // not an image type the app takes
        // A length that lies does not get past the limit: the bytes are counted.
        var huge = UploadContent.FromStream("big.png", _ => Task.FromResult<Stream>(new MemoryStream(new byte[ImageEndpoints.MaxImageBytes + 1])), length: 10);
        Assert.False((await SendAsync(person, new AddImage { SpaceId = space, File = huge })).Ok);
        // An attachment and an image name who added them: a plugin acting as itself cannot add one.
        Assert.Equal(401, (await SendAsync(Actor.Plugin("kb.test.maker", ActorSources.Job), new AddAttachment { PageId = page, File = UploadContent.FromBytes("x.txt", Png) })).Problem!.Status);
    }
}
