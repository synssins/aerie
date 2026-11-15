using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The branding routes go through the action bus: each answers with its action's name, only an Admin changes the brand, and a plugin can refuse or shape them.</summary>
public sealed class BrandingPipelineTests : IAsyncLifetime
{
    private const string Admin = "/api/v1/admin";
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync($"{Admin}/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private static Task<HttpResponseMessage> RenameAsync(HttpClient c, string productName) => c.PutAsJsonAsync($"{Admin}/brand", new { productName }, Ct);
    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, string variant, HttpContent body) => c.PutAsync($"{Admin}/brand/logo/{variant}", body, Ct);
    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, string variant, byte[] body) => UploadAsync(c, variant, new ByteArrayContent(body));
    private Task<BrandDto?> BrandAsync() => _f.NewClient().GetFromJsonAsync<BrandDto>("/api/v1/brand", Ct);

    private async Task<int> HooksAsync(string name) =>
        (await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct)).GetProperty("byAction").TryGetProperty(name, out var n) ? n.GetInt32() : 0;

    /// <summary>A body whose size the request does not say.</summary>
    private sealed class Unsized(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
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
    public async Task EveryBrandingRoute_AnswersWithItsActionName()
    {
        var anonymous = _f.NewClient();

        // 200 with no body, as before: not 204.
        var renamed = await RenameAsync(_admin, "Help Hub");
        Assert.Equal((HttpStatusCode.OK, "brand.update", ""), (renamed.StatusCode, Action(renamed), await renamed.Content.ReadAsStringAsync(Ct)));
        var set = await UploadAsync(_admin, "light", Png);
        Assert.Equal((HttpStatusCode.OK, "brand.logo.set", ""), (set.StatusCode, Action(set), await set.Content.ReadAsStringAsync(Ct)));

        // Both reads are open without sign-in.
        var brand = await anonymous.GetAsync("/api/v1/brand", Ct);
        Assert.Equal((HttpStatusCode.OK, "brand.read"), (brand.StatusCode, Action(brand)));
        var json = await brand.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["productName", "logoLightUrl", "logoDarkUrl", "primaryColor", "signInNote", "theme"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Help Hub", json.GetProperty("productName").GetString());
        Assert.StartsWith("/api/v1/brand/logo/light?v=", json.GetProperty("logoLightUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("logoDarkUrl").ValueKind);

        // The file is answered as before: its own type and length, cached for a day, and unable to run anything.
        var logo = await anonymous.GetAsync(json.GetProperty("logoLightUrl").GetString(), Ct);
        Assert.Equal((HttpStatusCode.OK, "brand.logo.read"), (logo.StatusCode, Action(logo)));
        Assert.Equal(("image/png", Png.Length), (logo.Content.Headers.ContentType?.ToString(), logo.Content.Headers.ContentLength));
        Assert.Equal("public, max-age=86400", logo.Headers.CacheControl?.ToString());
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", logo.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Null(logo.Content.Headers.ContentDisposition);
        Assert.False(logo.Headers.Contains("Accept-Ranges"));
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync(Ct));

        var removed = await _admin.DeleteAsync($"{Admin}/brand/logo/light", Ct);
        Assert.Equal((HttpStatusCode.NoContent, "brand.logo.remove"), (removed.StatusCode, Action(removed)));
        var removedAgain = await _admin.DeleteAsync($"{Admin}/brand/logo/light", Ct); // nothing to remove is 204 too
        Assert.Equal((HttpStatusCode.NoContent, "brand.logo.remove"), (removedAgain.StatusCode, Action(removedAgain)));

        // A refusal by the core names the action too.
        var missing = await anonymous.GetAsync("/api/v1/brand/logo/light", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "brand.logo.read"), (missing.StatusCode, Action(missing)));
        var noVariant = await UploadAsync(_admin, "sepia", Png);
        Assert.Equal((HttpStatusCode.NotFound, "brand.logo.set"), (noVariant.StatusCode, Action(noVariant)));
        var blank = await RenameAsync(_admin, " ");
        Assert.Equal((HttpStatusCode.BadRequest, "brand.update"), (blank.StatusCode, Action(blank)));
        Assert.Equal("1-60 characters.", (await blank.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("productName")[0].GetString());
        var notAnImage = await UploadAsync(_admin, "dark", "MZ not an image"u8.ToArray());
        Assert.Equal((HttpStatusCode.BadRequest, "brand.logo.set"), (notAnImage.StatusCode, Action(notAnImage)));
        Assert.Equal("PNG, JPEG, WebP or SVG only.", (await notAnImage.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("file")[0].GetString());
    }

    [Fact]
    public async Task EveryAdminRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        (await UploadAsync(_admin, "dark", Png)).EnsureSuccessStatusCode();
        var user = await UserAsync();

        List<(string Name, HttpResponseMessage Answer)> answers =
        [
            ("brand.update", await RenameAsync(user, "Mine")),
            ("brand.logo.set", await UploadAsync(user, "light", Png)),
            ("brand.logo.set", await UploadAsync(user, "sepia", Png)), // 403 comes before the 404 of a variant that does not exist
            ("brand.logo.remove", await user.DeleteAsync($"{Admin}/brand/logo/dark", Ct)),
        ];

        // The handler refuses, not a filter in front of it: the answer names the action.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        var brand = await BrandAsync();
        Assert.NotEqual("Mine", brand!.ProductName);
        Assert.Null(brand.LogoLightUrl);
        Assert.NotNull(brand.LogoDarkUrl);
        // Without sign-in the answer is 401, before any action.
        var anonymous = await RenameAsync(_f.NewClient(), "Mine");
        Assert.Equal((HttpStatusCode.Unauthorized, null), (anonymous.StatusCode, Action(anonymous)));
    }

    [Fact]
    public async Task Rename_RefusedByHook_Is422_NameUnchanged_NoAuditEntry()
    {
        var before = (await BrandAsync())!.ProductName;
        await SetAsync("Refuse", "brand.update");

        var r = await RenameAsync(_admin, "Help Hub");

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Equal(before, (await BrandAsync())!.ProductName);
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.BrandUpdate, Ct)));
    }

    [Fact]
    public async Task Read_FilterShapesTheResult()
    {
        (await RenameAsync(_admin, "Help Hub")).EnsureSuccessStatusCode();
        await SetAsync("Blank", "brand.read:ProductName");

        Assert.Equal("", (await BrandAsync())!.ProductName);

        // The filter shapes the answer, not the setting.
        await SetAsync("Blank", "");
        Assert.Equal("Help Hub", (await BrandAsync())!.ProductName);
    }

    [Fact]
    public async Task Logo_Over5MB_Is400_BeforeAnyHook()
    {
        (await UploadAsync(_admin, "light", Png)).EnsureSuccessStatusCode();
        Assert.Equal(1, await HooksAsync("before:brand.logo.set"));
        var big = new byte[5 * 1_048_576 + 1];
        Png.CopyTo(big, 0);

        var declared = await UploadAsync(_admin, "dark", big);
        var undeclared = await UploadAsync(_admin, "dark", new Unsized(big));

        foreach (var r in new[] { declared, undeclared })
        {
            Assert.Equal((HttpStatusCode.BadRequest, "brand.logo.set"), (r.StatusCode, Action(r)));
            Assert.Equal("5 MB maximum.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("file")[0].GetString());
        }
        Assert.Equal(1, await HooksAsync("before:brand.logo.set")); // no plugin was handed either file
        Assert.Null((await BrandAsync())!.LogoDarkUrl);
    }

    [Fact]
    public async Task Writes_AreAudited_AsTheCaller()
    {
        (await RenameAsync(_admin, "  Help Hub ")).EnsureSuccessStatusCode();
        (await UploadAsync(_admin, "light", Png)).EnsureSuccessStatusCode();
        (await UploadAsync(_admin, "light", Png)).EnsureSuccessStatusCode(); // a second upload replaces the first
        (await _admin.DeleteAsync($"{Admin}/brand/logo/light", Ct)).EnsureSuccessStatusCode();
        (await _admin.DeleteAsync($"{Admin}/brand/logo/light", Ct)).EnsureSuccessStatusCode(); // nothing removed: no entry

        await _f.WithDbAsync(async db =>
        {
            var me = (await db.Users.SingleAsync(u => u.IsBreakGlass, Ct)).Id;
            var entries = await db.AuditEntries.Where(a => a.Action == AuditActions.BrandUpdate).OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal(
            [
                (me, "brand", "productName", """{"productName":"Help Hub"}"""),
                (me, "brand", "logo.light", """{"type":"image/png","bytes":"16"}"""),
                (me, "brand", "logo.light", """{"type":"image/png","bytes":"16"}"""),
                (me, "brand", "logo.light", """{"removed":"true"}"""),
            ], entries.Select(a => (a.ActorId!.Value, a.TargetType!, a.TargetId!, a.DataJson)));
            var stored = await db.RuntimeSettings.SingleAsync(s => s.Key == BrandingEndpoints.ProductNameKey, Ct);
            Assert.Equal(("Help Hub", me), (stored.Value, stored.UpdatedBy));
            Assert.False(await db.BrandAssets.AnyAsync(Ct));
        });
    }
}
