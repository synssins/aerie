using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The image routes go through the action bus: each answers with its action's name, a plugin can refuse them, and no plugin sees a file that is too large.</summary>
public sealed class ImagePipelineTests : IAsyncLifetime
{
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private readonly string _files;
    private KbAppFactory _f;
    private HttpClient _admin = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ImagePipelineTests()
    {
        var db = KbAppFactory.NewDbPath();
        _files = db + ".files";
        _f = new(db, settings: new Dictionary<string, string> { ["Kb:Storage:Folder"] = _files });
    }

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

    private Task<HttpResponseMessage> UploadAsync(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.Add("X-File-Name", "shot.png");
        return _admin.PostAsync($"/api/v1/spaces/{_space}/images", content, Ct);
    }

    private Task<HttpResponseMessage> UploadAsync(byte[] body) => UploadAsync(new ByteArrayContent(body));

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private async Task<int> HooksAsync(string name) =>
        (await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct)).GetProperty("byAction").TryGetProperty(name, out var n) ? n.GetInt32() : 0;

    /// <summary>A body whose size the request does not say.</summary>
    private sealed class Unsized(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task EveryImageRoute_AnswersWithItsActionName()
    {
        var added = await UploadAsync(Png);
        Assert.Equal((HttpStatusCode.Created, "image.add"), (added.StatusCode, Action(added)));
        var img = (await added.Content.ReadFromJsonAsync<ImageDto>(Ct))!;
        Assert.Equal(($"/api/v1/images/{img.Id}", img.Url), (img.Url, added.Headers.Location?.ToString()));

        // The file is answered as before: its own type, cached for good, and unable to run anything.
        var read = await _admin.GetAsync(img.Url, Ct);
        Assert.Equal((HttpStatusCode.OK, "image.read"), (read.StatusCode, Action(read)));
        Assert.Equal("image/png", read.Content.Headers.ContentType?.ToString());
        Assert.Equal("max-age=31536000, private, immutable", read.Headers.CacheControl?.ToString()); // as HttpClient writes "private, max-age=31536000, immutable"
        Assert.Equal("default-src 'none'; sandbox", read.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Null(read.Content.Headers.ContentDisposition);
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync(Ct));

        var imported = await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/images/import", new { url = "file:///c:/windows/win.ini" }, Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "image.import"), (imported.StatusCode, Action(imported)));
        Assert.Equal("An http or https address.", (await imported.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("url")[0].GetString());
        var blocked = await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/images/import", new { url = "http://127.0.0.1/logo.png" }, Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "image.import"), (blocked.StatusCode, Action(blocked)));
        Assert.Equal("Image could not be fetched.", (await blocked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("url")[0].GetString());

        // A refusal by the core names the action too.
        var missing = await _admin.GetAsync($"/api/v1/images/{Guid.NewGuid()}", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "image.read"), (missing.StatusCode, Action(missing)));
        var noSpace = await _admin.PostAsync($"/api/v1/spaces/{Guid.NewGuid()}/images", new ByteArrayContent(Png), Ct);
        Assert.Equal((HttpStatusCode.NotFound, "image.add"), (noSpace.StatusCode, Action(noSpace)));
    }

    [Fact]
    public async Task Add_RefusedByHook_Is422_NothingStored()
    {
        await SetAsync("Refuse", "image.add");

        var r = await UploadAsync(Png);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        await _f.WithDbAsync(async db => Assert.False(await db.SpaceImages.AnyAsync(Ct)));
        Assert.False(Directory.Exists(_files) && Directory.EnumerateFiles(_files, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Add_Over20MB_Is400_BeforeAnyHook()
    {
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(Png)).StatusCode);
        Assert.Equal(1, await HooksAsync("before:image.add"));
        var big = new byte[20 * 1_048_576 + 1];
        Png.CopyTo(big, 0);

        var declared = await UploadAsync(big);
        var undeclared = await UploadAsync(new Unsized(big));

        foreach (var r in new[] { declared, undeclared })
        {
            Assert.Equal((HttpStatusCode.BadRequest, "image.add"), (r.StatusCode, Action(r)));
            Assert.Equal("20 MB maximum.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("file")[0].GetString());
        }
        Assert.Equal(1, await HooksAsync("before:image.add")); // no plugin was handed either file
        await _f.WithDbAsync(async db => Assert.Equal(1, await db.SpaceImages.CountAsync(Ct)));
    }

    [Fact]
    public async Task Read_FilterShapesTheResult()
    {
        var img = (await (await UploadAsync(Png)).Content.ReadFromJsonAsync<ImageDto>(Ct))!;
        await SetAsync("Blank", "image.read:ContentType");

        var r = await _admin.GetAsync(img.Url, Ct);

        Assert.Equal((HttpStatusCode.OK, null), (r.StatusCode, r.Content.Headers.ContentType));
        Assert.Equal("default-src 'none'; sandbox", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(Png, await r.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Read_FilterThrows_Is422_NothingIsAnswered_TheFileIsClosed()
    {
        var img = (await (await UploadAsync(Png)).Content.ReadFromJsonAsync<ImageDto>(Ct))!;
        await SetAsync("FailFilter", "image.read");

        var r = await _admin.GetAsync(img.Url, Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.NotEqual("image/png", r.Content.Headers.ContentType?.MediaType);
        Assert.False((await r.Content.ReadAsByteArrayAsync(Ct)).AsSpan().StartsWith(Png));
        // The handler had opened the file before the filter failed. Nothing holds it once the request is over.
        var stored = Assert.Single(Directory.EnumerateFiles(_files, "*", SearchOption.AllDirectories));
        for (var waited = 0; ; waited += 50)
        {
            try { await using var alone = new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.None); break; }
            catch (IOException) when (waited < 5000) { await Task.Delay(50, Ct); }
        }
    }
}
