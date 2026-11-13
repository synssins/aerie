using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kb.Tests.Integration;

/// <summary>
/// Moving files between two folders. Except where a test says so, the background copy is taken out of the app and
/// the test runs it itself, so "while the copy runs" is a fixed point and not a race.
/// </summary>
public sealed class StorageMoveTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/storage";
    private readonly string _dbPath = KbAppFactory.NewDbPath();
    private readonly string _from;
    private readonly string _to = NewFolder();
    private readonly KbAppFactory _f;

    public StorageMoveTests()
    {
        _from = _dbPath + ".files";
        _f = Instance();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Png2 = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), $"kb-test-{Guid.NewGuid():N}.place");

    /// <summary>An app on this test's database. Two of them are two servers.</summary>
    private KbAppFactory Instance(bool backgroundCopy = false) => new(_dbPath,
        settings: new Dictionary<string, string> { ["Kb:Storage:Folder"] = _from },
        services: backgroundCopy ? null : s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null).ToList()) s.Remove(d);
        });

    private static string In(string folder, Guid id) => Path.Combine(folder, id.ToString("N")[..2], id.ToString("N"));

    private static async Task<Guid> SpaceAsync(HttpClient admin) =>
        (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk " + Guid.NewGuid().ToString("N")[..6] }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;

    private static async Task<Guid> UploadAsync(HttpClient c, Guid spaceId, byte[]? body = null)
    {
        var content = new ByteArrayContent(body ?? Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var r = await c.PostAsync($"/api/v1/spaces/{spaceId}/images", content, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ImageDto>(Ct))!.Id;
    }

    private static async Task<Guid> AttachAsync(HttpClient c, Guid spaceId)
    {
        var page = await (await c.PostAsJsonAsync($"/api/v1/spaces/{spaceId}/pages", new { kind = "Page", title = "Guide", contentJson = """{"type":"doc","content":[]}""" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var content = new ByteArrayContent("attached text"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.Add("X-File-Name", "notes.txt");
        var r = await c.PostAsync($"/api/v1/pages/{page.GetProperty("id").GetString()}/attachments", content, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<AttachmentDto>(Ct))!.Id;
    }

    private async Task<string> AddPlaceAsync(HttpClient admin, string? path = null, string name = "New")
    {
        var r = await admin.PostAsJsonAsync($"{Url}/places", new { name, kind = "folder", settings = new { path = path ?? _to } }, Ct);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private async Task<string> StartAsync(HttpClient admin)
    {
        var toId = await AddPlaceAsync(admin);
        var r = await admin.PostAsJsonAsync($"{Url}/move", new { toId }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return toId;
    }

    private static async Task<JsonElement> MoveAsync(HttpClient admin) => (await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("move");

    private static async Task<string?> RoleAsync(HttpClient admin, string placeId) =>
        (await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("places").EnumerateArray().Single(p => p.GetProperty("id").GetString() == placeId).GetProperty("role").GetString();

    private static Task<bool> CopyAsync(KbAppFactory f) => f.Services.GetRequiredService<StorageMover>().RunOnceAsync(Ct);

    private static (int Total, int Copied, int Checked, int Failed) Counts(JsonElement move) =>
        (move.GetProperty("total").GetInt32(), move.GetProperty("copied").GetInt32(), move.GetProperty("checked").GetInt32(), move.GetProperty("failed").GetInt32());

    [Fact]
    public async Task Move_CopiesEveryFile_ThenChecksThem()
    {
        await using var f = Instance(backgroundCopy: true); // the real background copy
        var admin = await f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        Guid[] files = [await UploadAsync(admin, space), await UploadAsync(admin, space, Png2), await AttachAsync(admin, space)];

        await StartAsync(admin);
        var move = await MoveAsync(admin);
        for (var i = 0; i < 100 && move.GetProperty("state").GetString() != "Copied"; i++)
        {
            await Task.Delay(200, Ct);
            move = await MoveAsync(admin);
        }

        Assert.Equal("Copied", move.GetProperty("state").GetString());
        Assert.Equal((3, 3, 3, 0), Counts(move));
        Assert.All(files, id => Assert.Equal(File.ReadAllBytes(In(_from, id)), File.ReadAllBytes(In(_to, id))));
    }

    [Fact]
    public async Task Move_UploadDuringCopy_IsInBoth()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var before = await UploadAsync(admin, space);
        await StartAsync(admin);

        var during = await UploadAsync(admin, space); // the copy has not reached anything yet
        Assert.True(File.Exists(In(_from, during)));
        Assert.True(File.Exists(In(_to, during)));

        Assert.True(await CopyAsync(_f));
        var move = await MoveAsync(admin);
        Assert.Equal("Copied", move.GetProperty("state").GetString());
        Assert.Equal((2, 2, 2, 0), Counts(move));
        Assert.True(File.Exists(In(_to, before)));
    }

    [Fact]
    public async Task Move_UploadThatCannotReachTheNewPlace_IsKept_AndBlocksTheSwitch()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        await StartAsync(admin);
        await CopyAsync(_f);
        Directory.Delete(_to, recursive: true);
        await File.WriteAllTextAsync(_to, "", Ct); // the new place is gone: a file stands where its folder was

        var id = await UploadAsync(admin, space);

        Assert.Equal(Png, await admin.GetByteArrayAsync($"/api/v1/images/{id}", Ct));
        var move = await MoveAsync(admin);
        Assert.Equal(1, move.GetProperty("failed").GetInt32());
        Assert.StartsWith(id.ToString(), move.GetProperty("lastError").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{Url}/move/switch", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Move_DeleteDuringCopy_IsGoneFromBoth()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        await StartAsync(admin);
        var id = await UploadAsync(admin, space); // no page uses it, so cleanup removes it
        Assert.True(File.Exists(In(_to, id)));

        await using var scope = _f.Services.CreateAsyncScope();
        var removed = await ImageEndpoints.CleanupAsync(scope.ServiceProvider.GetRequiredService<KbDbContext>(), scope.ServiceProvider.GetRequiredService<Kb.Plugins.IFileStore>(), DateTimeOffset.UtcNow.AddDays(8), Ct);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(In(_from, id)));
        Assert.False(File.Exists(In(_to, id)));
    }

    [Fact]
    public async Task Move_FileMissingAtSource_IsFailed_SwitchRefused()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var lost = await UploadAsync(admin, space);
        await UploadAsync(admin, space);
        File.Delete(In(_from, lost));
        var toId = await StartAsync(admin);

        await CopyAsync(_f);
        var move = await MoveAsync(admin);
        Assert.Equal(1, move.GetProperty("failed").GetInt32());
        Assert.StartsWith(lost.ToString(), move.GetProperty("lastError").GetString());

        var r = await admin.PostAsync($"{Url}/move/switch", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("Not everything was copied.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal("Target", await RoleAsync(admin, toId));
    }

    [Fact]
    public async Task CopyAgain_CopiesOnlyWhatIsMissing()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var lost = await UploadAsync(admin, space);
        var kept = await UploadAsync(admin, space);
        File.Move(In(_from, lost), In(_from, lost) + ".away");
        await StartAsync(admin);
        await CopyAsync(_f);
        Assert.Equal(1, (await MoveAsync(admin)).GetProperty("failed").GetInt32());

        File.Move(In(_from, lost) + ".away", In(_from, lost));
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(In(_to, kept), old); // a second copy of this file would change the time
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Url}/move/copy-again", null, Ct)).StatusCode);
        Assert.Equal("Copying", (await MoveAsync(admin)).GetProperty("state").GetString());
        await CopyAsync(_f);

        var move = await MoveAsync(admin);
        Assert.Equal("Copied", move.GetProperty("state").GetString());
        Assert.Equal((2, 2, 2, 0), Counts(move));
        Assert.Equal(JsonValueKind.Null, move.GetProperty("lastError").ValueKind);
        Assert.True(File.Exists(In(_to, lost)));
        Assert.Equal(old, File.GetLastWriteTimeUtc(In(_to, kept)));
    }

    [Fact]
    public async Task Preview_OnlyThatAdminReadsFromTheNewPlace()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var id = await UploadAsync(admin, space);
        await StartAsync(admin);
        await CopyAsync(_f);
        await File.WriteAllBytesAsync(In(_to, id), Png2, Ct); // same size, other content: shows which place answered

        Guid readerId;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var u = new KbUser { UserName = "reader", DisplayName = "Reader", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(u, "user-password-0123456789")).Succeeded);
            readerId = u.Id;
        }
        await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = readerId.ToString(), role = "Reader" }, Ct);
        var reader = _f.NewClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "reader", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"{Url}/move/preview", new { on = true }, Ct)).StatusCode);
        Assert.True((await MoveAsync(admin)).GetProperty("preview").GetBoolean());
        Assert.Equal(Png2, await admin.GetByteArrayAsync($"/api/v1/images/{id}", Ct));
        Assert.Equal(Png, await reader.GetByteArrayAsync($"/api/v1/images/{id}", Ct));

        await admin.PutAsJsonAsync($"{Url}/move/preview", new { on = false }, Ct);
        Assert.Equal(Png, await admin.GetByteArrayAsync($"/api/v1/images/{id}", Ct));
    }

    [Fact]
    public async Task Switch_MakesTheNewPlaceActive_OldFilesUntouched()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var id = await UploadAsync(admin, space);
        var body = await admin.GetFromJsonAsync<JsonElement>(Url, Ct);
        var fromId = body.GetProperty("places").EnumerateArray().Single().GetProperty("id").GetString()!;
        var toId = await StartAsync(admin);
        await CopyAsync(_f);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Url}/move/switch", null, Ct)).StatusCode);

        Assert.Equal("Active", await RoleAsync(admin, toId));
        Assert.Equal("Old", await RoleAsync(admin, fromId));
        var move = await MoveAsync(admin);
        Assert.Equal("Switched", move.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, move.GetProperty("finishedAt").ValueKind);
        Assert.Equal(Png, await File.ReadAllBytesAsync(In(_from, id), Ct));
        Assert.Equal(Png, await admin.GetByteArrayAsync($"/api/v1/images/{id}", Ct));

        var after = await UploadAsync(admin, space);
        Assert.True(File.Exists(In(_to, after)));
        Assert.False(File.Exists(In(_from, after)));
    }

    [Fact]
    public async Task Switch_ChangesNoPageImageOrAttachmentRow()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        await UploadAsync(admin, space);
        await AttachAsync(admin, space);

        async Task<string> RowsAsync()
        {
            string rows = "";
            await _f.WithDbAsync(async db => rows = JsonSerializer.Serialize(new
            {
                pages = await db.Pages.AsNoTracking().OrderBy(p => p.Id).ToListAsync(Ct),
                versions = await db.PageVersions.AsNoTracking().OrderBy(v => v.Id).ToListAsync(Ct),
                images = await db.SpaceImages.AsNoTracking().OrderBy(i => i.Id).ToListAsync(Ct),
                attachments = await db.Attachments.AsNoTracking().OrderBy(a => a.Id).ToListAsync(Ct),
            }));
            return rows;
        }

        var before = await RowsAsync();
        await StartAsync(admin);
        await CopyAsync(_f);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Url}/move/switch", null, Ct)).StatusCode);
        Assert.Equal(before, await RowsAsync());
    }

    [Fact]
    public async Task SecondInstance_WritesToTheNewPlace_RightAfterTheSwitch()
    {
        await using var second = Instance();
        var admin = await _f.SignedInBreakGlassAsync();
        var admin2 = await second.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var early = await UploadAsync(admin2, space); // the second server has used the old place
        Assert.Equal(Png, await admin2.GetByteArrayAsync($"/api/v1/images/{early}", Ct));
        Assert.True(File.Exists(In(_from, early)));
        await StartAsync(admin);
        await CopyAsync(_f);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Url}/move/switch", null, Ct)).StatusCode);
        var late = await UploadAsync(admin2, space);

        Assert.True(File.Exists(In(_to, late)));
        Assert.False(File.Exists(In(_from, late)));
        Assert.Equal(Png, await admin2.GetByteArrayAsync($"/api/v1/images/{late}", Ct)); // read at once, before its 5 seconds are over
    }

    [Fact]
    public async Task TwoInstances_OnlyOneCopies()
    {
        await using var second = Instance();
        var admin = await _f.SignedInBreakGlassAsync();
        await second.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var id = await UploadAsync(admin, space);
        await StartAsync(admin);
        var moveId = (await MoveAsync(admin)).GetProperty("id").GetGuid();

        Assert.True(await _f.Services.GetRequiredService<StorageMover>().ClaimAsync(moveId, Ct)); // the first server is copying
        Assert.False(await CopyAsync(second));
        Assert.False(File.Exists(In(_to, id)));
        Assert.Equal("Copying", (await MoveAsync(admin)).GetProperty("state").GetString());

        Assert.True(await CopyAsync(_f));
        Assert.True(File.Exists(In(_to, id)));
        Assert.Equal("Copied", (await MoveAsync(admin)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Cancel_LeavesTheActivePlaceAsItWas()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        var id = await UploadAsync(admin, space);
        var fromId = (await admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("places").EnumerateArray().Single().GetProperty("id").GetString()!;
        var toId = await StartAsync(admin);
        await CopyAsync(_f);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Url}/move/cancel", null, Ct)).StatusCode);

        Assert.Equal("Active", await RoleAsync(admin, fromId));
        Assert.Equal("Old", await RoleAsync(admin, toId));
        Assert.Equal("Cancelled", (await MoveAsync(admin)).GetProperty("state").GetString());
        Assert.True(File.Exists(In(_to, id))); // what was copied stays
        var after = await UploadAsync(admin, space);
        Assert.True(File.Exists(In(_from, after)));
        Assert.False(File.Exists(In(_to, after)));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{Url}/move/switch", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Move_ToAPlaceWhoseTestFails_409_WithTheError()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var broken = await AddPlaceAsync(admin, Path.Combine(Path.GetTempFileName(), "sub"), "Broken");
        var r = await admin.PostAsJsonAsync($"{Url}/move", new { toId = broken }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.NotEqual("", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal("Old", await RoleAsync(admin, broken));
        Assert.Equal(JsonValueKind.Null, (await MoveAsync(admin)).ValueKind);

        await StartAsync(admin);
        var again = await admin.PostAsJsonAsync($"{Url}/move", new { toId = await AddPlaceAsync(admin, NewFolder(), "Third") }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("A move is already running.", (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task MoveRoutes_NotAdmin_403()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "tech", DisplayName = "Tech", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "tech", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"{Url}/move", new { toId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"{Url}/move/copy-again", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"{Url}/move/preview", new { on = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"{Url}/move/switch", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"{Url}/move/cancel", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Moves_AreAudited()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = await SpaceAsync(admin);
        await UploadAsync(admin, space);
        var toId = await StartAsync(admin);
        await admin.PostAsync($"{Url}/move/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Url}/move", new { toId }, Ct)).StatusCode);
        await CopyAsync(_f);
        await admin.PostAsync($"{Url}/move/switch", null, Ct);

        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action.StartsWith("storage.move.")).OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal(["storage.move.start", "storage.move.cancel", "storage.move.start", "storage.move.switch"], entries.Select(e => e.Action));
            Assert.All(entries, e =>
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(e.DataJson)!;
                Assert.Equal(("Web server folder", "New"), (data["from"], data["to"]));
                Assert.True(data.ContainsKey("copied") && data.ContainsKey("failed") && data.ContainsKey("total"));
            });
            Assert.Equal("1", JsonSerializer.Deserialize<Dictionary<string, string>>(entries[^1].DataJson)!["copied"]);
        });
    }
}
