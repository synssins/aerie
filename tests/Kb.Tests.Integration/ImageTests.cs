using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Web;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

public sealed class ImageTests : IAsyncLifetime
{
    private readonly KbAppFactory _f;

    public ImageTests()
    {
        var db = KbAppFactory.NewDbPath();
        _f = new(db, settings: new Dictionary<string, string> { ["Kb:Storage:Folder"] = db + ".files" });
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<(HttpClient Admin, Guid SpaceId)> SpaceAsync()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var s = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        return (admin, s!.Id);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, Guid spaceId, byte[] body, string name = "shot.png")
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.Add("X-File-Name", name);
        return c.PostAsync($"/api/v1/spaces/{spaceId}/images", content, Ct);
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

    [Fact]
    public async Task Upload_Png_ThenReaderCanView()
    {
        var (admin, s) = await SpaceAsync();
        var r = await UploadAsync(admin, s, Png);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var img = await r.Content.ReadFromJsonAsync<ImageDto>(Ct);
        Assert.StartsWith("/api/v1/images/", img!.Url);

        var reader = await UserAsync("reader", s, SpaceRole.Reader);
        var get = await reader.GetAsync(img.Url, Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await get.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task View_WithoutSpaceAccess_Returns404()
    {
        var (admin, s) = await SpaceAsync();
        var img = await (await UploadAsync(admin, s, Png)).Content.ReadFromJsonAsync<ImageDto>(Ct);
        var outsider = await UserAsync("outsider", s, null);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(img!.Url, Ct)).StatusCode);
    }

    [Fact]
    public async Task Upload_ByReader_Returns403()
    {
        var (_, s) = await SpaceAsync();
        var reader = await UserAsync("reader", s, SpaceRole.Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(reader, s, Png)).StatusCode);
    }

    [Fact]
    public async Task Upload_NotAnImage_Returns400()
    {
        var (admin, s) = await SpaceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, s, "<svg onload=alert(1)>"u8.ToArray(), "x.svg")).StatusCode);
    }

    [Fact]
    public async Task Upload_Over20MB_Returns400()
    {
        var (admin, s) = await SpaceAsync();
        var big = new byte[20 * 1_048_576 + 1];
        Png.CopyTo(big, 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, s, big)).StatusCode);
    }

    [Fact]
    public async Task Upload_Exactly20MB_Succeeds()
    {
        var (admin, s) = await SpaceAsync();
        var max = new byte[20 * 1_048_576];
        Png.CopyTo(max, 0);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(admin, s, max)).StatusCode);
    }
}
