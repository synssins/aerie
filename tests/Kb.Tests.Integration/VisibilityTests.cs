using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Who sees a space: Private (its roles only), Signed-in users (everyone signed in reads it), Anyone (people who are not signed in read
/// it too). Only an Admin sets it; a sensitive space stays Private. Without sign-in only the read routes answer, and only for Anyone spaces.
/// </summary>
public sealed class VisibilityTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> SpaceAsync(string key)
    {
        var r = await _admin.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var dto = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct));
        Assert.Equal("Private", dto.GetProperty("visibility").GetString());
        return dto.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SetAsync(Guid space, string visibility, HttpClient? by = null) =>
        (by ?? _admin).PatchAsJsonAsync($"/api/v1/admin/spaces/{space}", new { visibility }, Ct);

    private async Task<HttpClient> UserAsync(string name, Guid? space = null, string? role = null)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        if (space is { } s) (await _admin.PutAsJsonAsync($"/api/v1/spaces/{s}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>A page with an image and an attachment in the space.</summary>
    private async Task<(Guid Page, Guid Image, string Attachment)> ContentAsync(Guid space)
    {
        var img = new ByteArrayContent(Png);
        img.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        img.Headers.Add("X-File-Name", "dot.png");
        var image = (await (await _admin.PostAsync($"/api/v1/spaces/{space}/images", img, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var doc = JsonSerializer.Serialize(new { type = "doc", content = new object[]
        {
            new { type = "paragraph", content = new[] { new { type = "text", text = "Reset the zebra router" } } },
            new { type = "image", attrs = new { src = $"/api/v1/images/{image}", alt = "dot" } },
        } });
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Zebra router", contentJson = doc }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var file = new ByteArrayContent("hello"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.Add("X-File-Name", "notes.txt");
        var attachment = (await (await _admin.PostAsync($"/api/v1/pages/{page}/attachments", file, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("url").GetString()!;
        return (page, image, attachment);
    }

    private static async Task<List<string>> SpaceNamesAsync(HttpClient c) =>
        [.. (await c.GetFromJsonAsync<JsonElement>("/api/v1/spaces", Ct)).EnumerateArray().Select(s => s.GetProperty("name").GetString()!)];

    [Fact]
    public async Task Anyone_IsReadWithoutSignIn_OnlyTheReadRoutes()
    {
        var open = await SpaceAsync("open");
        var (page, image, attachment) = await ContentAsync(open);
        var nobody = _f.NewClient();

        // Private: nothing is disclosed.
        Assert.Empty(await SpaceNamesAsync(nobody));
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/v1/spaces/{open}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(open, "Anyone")).StatusCode);
        Assert.Equal(["open"], await SpaceNamesAsync(nobody));
        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync($"/api/v1/spaces/{open}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync($"/api/v1/spaces/{open}/tree", Ct)).StatusCode);
        var read = await nobody.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct);
        Assert.Equal("Zebra router", read.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync($"/api/v1/images/{image}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync(attachment, Ct)).StatusCode);
        Assert.Contains("Zebra router", await nobody.GetStringAsync("/api/v1/search?q=zebra", Ct));

        // Everything else still needs sign-in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync($"/api/v1/pages/{page}/versions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync($"/api/v1/pages/{page}/markdown", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.PostAsJsonAsync($"/api/v1/spaces/{open}/pages", new { kind = "Page", title = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync("/api/v1/me", Ct)).StatusCode);

        // Back to Private: closed again.
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(open, "Private")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        Assert.DoesNotContain("Zebra router", await nobody.GetStringAsync("/api/v1/search?q=zebra", Ct));
    }

    [Fact]
    public async Task SignedIn_IsReadByEveryoneSignedIn_ButNotWithoutSignIn()
    {
        var team = await SpaceAsync("team");
        var (page, _, _) = await ContentAsync(team);
        var outsider = await UserAsync("otto");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(team, "SignedIn")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await outsider.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        Assert.Equal("Reader", (await outsider.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{team}", Ct)).GetProperty("myRole").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/v1/spaces/{team}/pages", new { kind = "Page", title = "x" }, Ct)).StatusCode);
        var nobody = _f.NewClient();
        Assert.Empty(await SpaceNamesAsync(nobody));
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
    }

    [Fact]
    public async Task History_IsForMembers_NotForWhoReadsOnlyBecauseOfVisibility()
    {
        var team = await SpaceAsync("history");
        var (page, _, _) = await ContentAsync(team);
        var doc = JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = "Second" } } } } });
        (await _admin.PutAsJsonAsync($"/api/v1/pages/{page}", new { title = "Zebra router", contentJson = doc, baseVersion = "1.0" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(team, "SignedIn")).StatusCode);
        var outsider = await UserAsync("nina");
        var reader = await UserAsync("rory", team, "Reader");

        // Not a member: the page, but no history from any route.
        var seen = await outsider.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct);
        Assert.Equal("Zebra router", seen.GetProperty("title").GetString());
        Assert.Empty(seen.GetProperty("history").EnumerateArray());
        Assert.Equal("", seen.GetProperty("updatedBy").GetString());
        Assert.False(seen.GetProperty("canSeeHistory").GetBoolean());
        foreach (var path in new[] { $"/api/v1/pages/{page}/versions", $"/api/v1/pages/{page}/versions/1.0", $"/api/v1/pages/{page}/diff?from=1.0&to=1.1" })
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(path, Ct)).StatusCode);

        // A Reader of the space and an Admin keep it.
        foreach (var member in new[] { reader, _admin })
        {
            var full = await member.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct);
            Assert.Equal(2, full.GetProperty("history").GetArrayLength());
            Assert.True(full.GetProperty("canSeeHistory").GetBoolean());
            Assert.NotEqual("", full.GetProperty("updatedBy").GetString());
            foreach (var path in new[] { $"/api/v1/pages/{page}/versions", $"/api/v1/pages/{page}/versions/1.0", $"/api/v1/pages/{page}/diff?from=1.0&to=1.1" })
                Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(path, Ct)).StatusCode);
        }

        // Someone not signed in reading an Anyone space is not a member either.
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(team, "Anyone")).StatusCode);
        var nobody = await _f.NewClient().GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct);
        Assert.Empty(nobody.GetProperty("history").EnumerateArray());
        Assert.Equal("", nobody.GetProperty("updatedBy").GetString());
        Assert.False(nobody.GetProperty("canSeeHistory").GetBoolean());
    }

    [Fact]
    public async Task OnlyAnAdmin_SetsVisibility_AndItIsAudited()
    {
        var mine = await SpaceAsync("mine");
        var owner = await UserAsync("olga", mine, "Owner");
        var r = await owner.PatchAsJsonAsync($"/api/v1/spaces/{mine}", new { visibility = "Anyone" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(mine, "Everybody")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(mine, "Anyone")).StatusCode);
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(SpaceVisibility.Anyone, (await db.Spaces.SingleAsync(s => s.Id == mine, Ct)).Visibility);
            Assert.Contains(await db.AuditEntries.Where(a => a.Action == AuditActions.SpaceUpdate && a.TargetId == mine.ToString()).Select(a => a.DataJson).ToListAsync(Ct),
                d => d.Contains("\"visibility\":\"Anyone\"", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ASensitiveSpace_StaysPrivate()
    {
        var hr = await SpaceAsync("hr");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{hr}", new { isSensitive = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetAsync(hr, "SignedIn")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetAsync(hr, "Anyone")).StatusCode);

        var open = await SpaceAsync("open");
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(open, "Anyone")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{open}", new { isSensitive = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "both", name = "both", isSensitive = true, visibility = "Anyone" }, Ct)).StatusCode);
        // Both at once, in the right order, is fine.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{open}", new { isSensitive = true, visibility = "Private" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task ARefusedToken_IsNotServedAsSomeoneNotSignedIn()
    {
        var open = await SpaceAsync("open");
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(open, "Anyone")).StatusCode);
        var bearer = _f.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "kbt_not_a_real_token_0123456789");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/v1/spaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync($"/api/v1/spaces/{open}", Ct)).StatusCode);
    }

    [Fact]
    public async Task TheServerWideSwitch_IsGone()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsync("/api/v1/admin/settings/public-space?enabled=true", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task RequestsWithoutSignIn_AreLimitedPerAddress()
    {
        await using var strict = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:AnonymousRequestsPerMinutePerAddress"] = "3" });
        var nobody = strict.NewClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) codes.Add((await nobody.GetAsync("/api/v1/spaces", Ct)).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], codes);
        // A signed-in person is not counted there.
        var admin = await strict.SignedInBreakGlassAsync();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/spaces", Ct)).StatusCode);
    }

    /// <summary>The upgrade: a public space stays readable by everyone signed in only when the old server-wide switch was on.</summary>
    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData(null, 0)]
    public async Task Upgrade_MapsTheOldPublicFlag(string? switchValue, int expected)
    {
        var path = KbAppFactory.NewDbPath();
        var options = new DbContextOptionsBuilder<KbDbContext>().UseSqlite($"Data Source={path}", s => s.MigrationsAssembly(KbDbContext.SqliteMigrations)).Options;
        await using (var db = new KbDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("UserCodes", Ct);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Spaces (Id, Key, Name, IsSensitive, IsPublic, CreatedAt, CommentsEnabled, AgingDays, AgingAudience) VALUES " +
                "('11111111-1111-1111-1111-111111111111', 'pub', 'Pub', 0, 1, 0, 1, 180, 0), ('22222222-2222-2222-2222-222222222222', 'priv', 'Priv', 0, 0, 0, 1, 180, 0)", Ct);
            if (switchValue is not null)
                await db.Database.ExecuteSqlRawAsync("INSERT INTO RuntimeSettings (Key, Value, UpdatedAt) VALUES ('PublicSpaceEnabled', {0}, 0)", [switchValue], Ct);
            await migrator.MigrateAsync(cancellationToken: Ct);
        }
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key, Visibility FROM Spaces ORDER BY Key";
        await using (var r = await cmd.ExecuteReaderAsync(Ct))
        {
            var rows = new List<(string, long)>();
            while (await r.ReadAsync(Ct)) rows.Add((r.GetString(0), r.GetInt64(1)));
            Assert.Equal([("priv", 0L), ("pub", (long)expected)], rows);
        }
        cmd.CommandText = "SELECT COUNT(*) FROM RuntimeSettings WHERE Key = 'PublicSpaceEnabled'";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }
}
