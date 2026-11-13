using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

public sealed class StorageTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/storage";
    private const string Password = "s3cret-share-password";
    private readonly KbAppFactory _f;
    private readonly string _folder;

    public StorageTests()
    {
        var db = KbAppFactory.NewDbPath();
        _folder = db + ".files";
        _f = new(db, settings: new Dictionary<string, string> { ["Kb:Storage:Folder"] = _folder });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), $"kb-test-{Guid.NewGuid():N}.place");

    private static Task<HttpResponseMessage> AddAsync(HttpClient c, string name, string? path, string kind = "folder", string? password = null) =>
        c.PostAsJsonAsync($"{Url}/places", new { name, kind, settings = new { path }, secrets = new { password } }, Ct);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> PlaceAsync(HttpClient c, string id) =>
        (await c.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("places").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);

    [Fact]
    public async Task FirstStart_CreatesTheActivePlace_FromConfig()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);

        // An image stored the way the app stored it before places existed.
        var id = Guid.NewGuid();
        var key = id.ToString("N");
        Directory.CreateDirectory(Path.Combine(_folder, key[..2]));
        await File.WriteAllBytesAsync(Path.Combine(_folder, key[..2], key), Png, Ct);
        await _f.WithDbAsync(async db =>
        {
            db.SpaceImages.Add(new SpaceImage { Id = id, SpaceId = space!.Id, FileName = "old.png", ContentType = "image/png", Size = Png.Length, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        var body = await admin.GetFromJsonAsync<JsonElement>(Url, Ct);
        var place = Assert.Single(body.GetProperty("places").EnumerateArray());
        Assert.Equal("folder", place.GetProperty("kind").GetString());
        Assert.Equal("Active", place.GetProperty("role").GetString());
        Assert.Equal("Web server folder", place.GetProperty("name").GetString());
        Assert.Equal(_folder, place.GetProperty("settings").GetProperty("path").GetString());
        Assert.Contains(body.GetProperty("kinds").EnumerateArray(), k => k.GetProperty("kind").GetString() == "folder");

        Assert.Equal(Png, await admin.GetByteArrayAsync($"/api/v1/images/{id}", Ct));
    }

    [Fact]
    public async Task Add_FolderPlace_ThenTest_Ok()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var folder = NewFolder();
        var r = await AddAsync(admin, "Second", folder);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var place = await JsonAsync(r);
        Assert.Equal("Old", place.GetProperty("role").GetString());

        var test = await JsonAsync(await admin.PostAsync($"{Url}/places/{place.GetProperty("id").GetString()}/test", null, Ct));
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Empty(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)); // the test file is removed again
    }

    [Fact]
    public async Task Test_UnwritablePath_AnswersTheError()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var file = Path.GetTempFileName(); // a folder cannot be made under a file
        var place = await JsonAsync(await AddAsync(admin, "Broken", Path.Combine(file, "sub")));
        var test = await JsonAsync(await admin.PostAsync($"{Url}/places/{place.GetProperty("id").GetString()}/test", null, Ct));
        Assert.False(test.GetProperty("ok").GetBoolean());
        Assert.NotEqual("", test.GetProperty("error").GetString());
        Assert.DoesNotContain('\n', test.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task Add_UnknownKind_400()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var r = await AddAsync(admin, "Tape", NewFolder(), kind: "tape");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await JsonAsync(r)).GetProperty("errors").TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task Add_MissingPath_400_NamesTheField()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var r = await AddAsync(admin, "No path", "  ");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await JsonAsync(r)).GetProperty("errors").TryGetProperty("path", out _));
    }

    [Fact]
    public async Task Secret_IsNeverReturned()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var add = await AddAsync(admin, "Share", NewFolder(), password: Password);
        var addBody = await add.Content.ReadAsStringAsync(Ct);
        var id = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString();
        var put = await admin.PutAsJsonAsync($"{Url}/places/{id}", new { name = "Share 2", settings = new { path = NewFolder(), userName = @"CORP\svc" }, secrets = new { password = Password } }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        string[] bodies =
        [
            addBody,
            await put.Content.ReadAsStringAsync(Ct),
            await admin.GetStringAsync(Url, Ct),
            await (await admin.PostAsync($"{Url}/places/{id}/test", null, Ct)).Content.ReadAsStringAsync(Ct),
        ];
        Assert.All(bodies, b => Assert.DoesNotContain(Password, b));
        Assert.Contains("password", (await PlaceAsync(admin, id!)).GetProperty("secretsSet").EnumerateArray().Select(s => s.GetString()));

        await _f.WithDbAsync(async db =>
        {
            var stored = await db.StoragePlaces.SingleAsync(p => p.Name == "Share 2", Ct);
            Assert.False(string.IsNullOrEmpty(stored.SecretsProtected));
            Assert.DoesNotContain(Password, stored.SecretsProtected);
            Assert.DoesNotContain(Password, stored.SettingsJson);
        });
    }

    [Fact]
    public async Task Secret_NullKeeps_EmptyClears()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var folder = NewFolder();
        var id = (await JsonAsync(await AddAsync(admin, "Share", folder, password: Password))).GetProperty("id").GetString()!;

        await admin.PutAsJsonAsync($"{Url}/places/{id}", new { name = "Share", settings = new { path = folder }, secrets = new { password = (string?)null } }, Ct);
        Assert.Single((await PlaceAsync(admin, id)).GetProperty("secretsSet").EnumerateArray());

        await admin.PutAsJsonAsync($"{Url}/places/{id}", new { name = "Share", settings = new { path = folder }, secrets = new { password = "" } }, Ct);
        Assert.Empty((await PlaceAsync(admin, id)).GetProperty("secretsSet").EnumerateArray());
    }

    [Fact]
    public async Task Uploads_GoToTheActivePlace()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        var elsewhere = NewFolder();
        // The active place, not the config value, decides where a file lands.
        await _f.WithDbAsync(async db =>
        {
            var active = await db.StoragePlaces.SingleAsync(p => p.Role == StorageRole.Active, Ct);
            active.SettingsJson = JsonSerializer.Serialize(new { path = elsewhere });
            await db.SaveChangesAsync(Ct);
        });

        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var img = await (await admin.PostAsync($"/api/v1/spaces/{space!.Id}/images", content, Ct)).Content.ReadFromJsonAsync<ImageDto>(Ct);

        var key = img!.Id.ToString("N");
        Assert.True(File.Exists(Path.Combine(elsewhere, key[..2], key)));
        Assert.False(File.Exists(Path.Combine(_folder, key[..2], key)));
        Assert.Equal(Png, await admin.GetByteArrayAsync(img.Url, Ct));
    }

    [Fact]
    public async Task Remove_ActivePlace_409()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var places = (await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("places");
        var active = places.EnumerateArray().Single().GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Url}/places/{active}", Ct)).StatusCode);

        var other = (await JsonAsync(await AddAsync(admin, "Second", NewFolder()))).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Url}/places/{other}", Ct)).StatusCode);
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("places").EnumerateArray());
    }

    [Fact]
    public async Task StorageRoutes_NotAdmin_403()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "tech", DisplayName = "Tech", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "tech", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(c, "Mine", NewFolder())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"{Url}/places/{id}", new { name = "x", settings = new { path = "x" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"{Url}/places/{id}/test", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"{Url}/places/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Changes_AreAudited_WithoutValues()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var folder = NewFolder();
        var id = (await JsonAsync(await AddAsync(admin, "Share", folder, password: Password))).GetProperty("id").GetString();
        await admin.PutAsJsonAsync($"{Url}/places/{id}", new { name = "Share 2", settings = new { path = folder, userName = @"CORP\svc-files" }, secrets = new { password = Password } }, Ct);
        await admin.DeleteAsync($"{Url}/places/{id}", Ct);

        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action.StartsWith("storage.place.")).OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal(["storage.place.add", "storage.place.change", "storage.place.remove"], entries.Select(e => e.Action));
            Assert.All(entries, e =>
            {
                Assert.Contains("folder", e.DataJson);
                Assert.Contains("Share", e.DataJson);
                Assert.DoesNotContain(Password, e.DataJson);
                Assert.DoesNotContain("svc-files", e.DataJson);
                Assert.DoesNotContain(Path.GetFileName(folder), e.DataJson);
            });
        });
    }
}
