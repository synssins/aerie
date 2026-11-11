using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

public sealed class BrandingTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static Task<HttpResponseMessage> Upload(HttpClient c, string variant, byte[] body, string type)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return c.PutAsync($"/api/v1/admin/brand/logo/{variant}", content, Ct);
    }

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
    public async Task Rename_ByAdmin_ChangesProductName()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/brand", new { productName = "Help Hub" }, Ct)).StatusCode);
        var b = await _f.NewClient().GetFromJsonAsync<BrandDto>("/api/v1/brand", Ct);
        Assert.Equal("Help Hub", b!.ProductName);
    }

    [Fact]
    public async Task Rename_ByNonAdmin_Returns403()
    {
        var u = await UserAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await u.PutAsJsonAsync("/api/v1/admin/brand", new { productName = "X" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Rename_Blank_Returns400()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/admin/brand", new { productName = " " }, Ct)).StatusCode);
    }

    [Fact]
    public async Task PngLogo_IsServedAnonymously_AndListedInBrand()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, "light", Png, "image/png")).StatusCode);

        var b = await _f.NewClient().GetFromJsonAsync<BrandDto>("/api/v1/brand", Ct);
        Assert.NotNull(b!.LogoLightUrl);
        Assert.Null(b.LogoDarkUrl);
        var r = await _f.NewClient().GetAsync(b.LogoLightUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await r.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Logo_NotAnImage_Returns400()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "light", Encoding.UTF8.GetBytes("MZ not an image"), "image/png")).StatusCode);
    }

    [Fact]
    public async Task SvgWithScript_Returns400()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "dark", svg, "image/svg+xml")).StatusCode);
    }

    [Fact]
    public async Task CleanSvg_IsServedWithSandboxPolicy()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#f2a516\"/></svg>");
        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, "dark", svg, "image/svg+xml")).StatusCode);
        var b = await _f.NewClient().GetFromJsonAsync<BrandDto>("/api/v1/brand", Ct);
        var r = await _f.NewClient().GetAsync(b!.LogoDarkUrl, Ct);
        Assert.Contains("sandbox", r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Logo_TooLarge_Returns400()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var big = new byte[5 * 1_048_576 + 1];
        Png.CopyTo(big, 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "light", big, "image/png")).StatusCode);
    }

    [Fact]
    public async Task DeleteLogo_RemovesIt()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await Upload(admin, "light", Png, "image/png");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/admin/brand/logo/light", Ct)).StatusCode);
        var b = await _f.NewClient().GetFromJsonAsync<BrandDto>("/api/v1/brand", Ct);
        Assert.Null(b!.LogoLightUrl);
    }
}
