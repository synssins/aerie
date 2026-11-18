using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Tests.Integration.DataCenter;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Findings M4 to M7 of the code review of 2026-11-17, on the compatible surface: an OAuth token is an Admin's only with the ADMIN scope, a
/// personal token limited to named spaces is not an Admin's, a caller who may not see a page's history gets none of it from the content read,
/// and a folder's body is held to the size of a page's.
/// </summary>
public sealed class CompatSurfaceTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _cora = null!;
    private Guid _space;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var cora = await UserAsync(_f, "cora");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = cora.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        _cora = Basic(_f, "cora");
        // Made an Admin after the session, the token or the consent exists: an Admin's own sign-in needs one-time codes.
        var boss = await UserAsync(_f, "boss");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = boss.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private Task PromoteBossAsync() => _f.WithDbAsync(async db =>
    {
        (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Users, u => u.UserName == "boss", Ct)).IsAdmin = true;
        await db.SaveChangesAsync(Ct);
    });

    private static object Page(string title, string body, string? parent = null) => parent is null
        ? new { type = "page", title, space = new { key = "OPS" }, body = new { storage = new { value = body, representation = "storage" } } }
        : new { type = "page", title, space = new { key = "OPS" }, ancestors = new[] { new { id = parent } }, body = new { storage = new { value = body, representation = "storage" } } };

    private async Task<string> CreateAsync(string title, string body = "<p>1</p>", string? parent = null) =>
        (await (await _cora.PostJsonAsync("/rest/api/content", Page(title, body, parent))).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;

    private HttpClient Bearer(string token)
    {
        var c = _f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    // ---- M4 ----

    [Fact]
    public async Task OAuthToken_OfAnAdmin_IsAnAdminsOnlyWithTheAdminScope()
    {
        var app = await OAuthKit.ClientAsync(_admin, scopes: ["READ", "WRITE", "ADMIN"]);
        var write = Bearer((await OAuthKit.TokensAsync(_f, app, "WRITE", "boss")).GetProperty("access_token").GetString()!);
        var admin = Bearer((await OAuthKit.TokensAsync(_f, app, "ADMIN", "boss")).GetProperty("access_token").GetString()!);
        await PromoteBossAsync();
        await (await write.PostJsonAsync("/rest/api/space", new { key = "NEW", name = "New" })).ErrorAsync(HttpStatusCode.Forbidden);
        await (await write.DeleteAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        // What WRITE names it still does: the Admin's own rights on content.
        await (await write.PostJsonAsync("/rest/api/content", Page("By the client", "<p>x</p>"))).JsonAsync(HttpStatusCode.OK);

        await (await admin.PostJsonAsync("/rest/api/space", new { key = "NEW", name = "New" })).JsonAsync(HttpStatusCode.OK);
    }

    // ---- M5 ----

    [Fact]
    public async Task PersonalToken_OfAnAdmin_LimitedToASpace_IsNotAnAdmins()
    {
        var boss = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await boss.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "boss", password = Password }, Ct)).StatusCode);
        async Task<HttpClient> TokenAsync(Guid? space) => Bearer((await (await boss.PostAsJsonAsync("/api/v1/tokens",
            new { name = "T " + Guid.NewGuid().ToString("N")[..6], kind = "Personal", scopes = new[] { new { spaceId = space, access = "Write" } }, days = 30 }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("token").GetString()!);

        var (scoped, everywhere) = (await TokenAsync(_space), await TokenAsync(null));
        await PromoteBossAsync();
        await (await scoped.PostJsonAsync("/rest/api/space", new { key = "NEW", name = "New" })).ErrorAsync(HttpStatusCode.Forbidden);
        await (await scoped.DeleteAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        // In its space it still writes.
        await (await scoped.PostJsonAsync("/rest/api/content", Page("By the script", "<p>x</p>"))).JsonAsync(HttpStatusCode.OK);

        await (await everywhere.PostJsonAsync("/rest/api/space", new { key = "NEW", name = "New" })).JsonAsync(HttpStatusCode.OK);
    }

    // ---- M6 ----

    [Theory]
    [InlineData("SignedIn")]
    [InlineData("Anyone")]
    public async Task ContentRead_ForSomeoneWhoMayNotSeeHistory_HasNoHistoryAndNamesNobody(string visibility)
    {
        var id = await CreateAsync("Open runbook");
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{id}", new { type = "page", title = "Open runbook", version = new { number = 2, message = "secret note" },
            body = new { storage = new { value = "<p>2</p>", representation = "storage" } } }, Ct)).JsonAsync(HttpStatusCode.OK);
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { visibility }, Ct)).EnsureSuccessStatusCode();
        await UserAsync(_f, "nils");
        var reader = visibility == "Anyone" ? _f.NewClient() : Basic(_f, "nils");
        const string expand = "?expand=body.storage,version,history,history.lastUpdated,history.previousVersion,history.contributors.publishers.users";

        foreach (var path in new[] { $"/rest/api/content/{id}", $"/rest/api/content/{id}{expand}" })
        {
            var text = await (await reader.GetAsync(path, Ct)).Content.ReadAsStringAsync(Ct);
            var c = JsonDocument.Parse(text).RootElement;
            Assert.False(c.TryGetProperty("history", out _), text);
            Assert.False(c.GetProperty("_expandable").TryGetProperty("history", out _), text);
            Assert.DoesNotContain("cora", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret note", text, StringComparison.Ordinal);
            // The version itself stays: its number and date, with the unknown user and no note.
            var version = c.GetProperty("version");
            Assert.Equal(2, version.GetProperty("number").GetInt32());
            Assert.Equal("", version.GetProperty("message").GetString());
            Assert.Equal("unknown", version.GetProperty("by").GetProperty("type").GetString());
        }
        // A list is the same content objects.
        Assert.DoesNotContain("cora", await (await reader.GetAsync("/rest/api/content?spaceKey=OPS&expand=version,history", Ct)).Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        // A member of the space keeps all of it.
        var mine = await _cora.GetJsonAsync($"/rest/api/content/{id}{expand}");
        Assert.Equal("cora", mine.GetProperty("version").GetProperty("by").GetProperty("username").GetString());
        Assert.Equal("secret note", mine.GetProperty("version").GetProperty("message").GetString());
        Assert.Equal("cora", mine.GetProperty("history").GetProperty("createdBy").GetProperty("username").GetString());
    }

    // ---- M7 ----

    [Fact]
    public async Task FolderBody_OverThePageLimit_Is400()
    {
        var folder = await CreateAsync("Guides");
        await CreateAsync("Child", parent: folder); // the page becomes a folder
        var big = "<p>" + new string('x', 5_000_000) + "</p>";
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{folder}", new { type = "page", title = "Guides", version = new { number = 2 }, body = new { storage = new { value = big, representation = "storage" } } }, Ct))
            .ErrorAsync(HttpStatusCode.BadRequest);
        await (await _cora.PutAsJsonAsync($"/rest/api/content/{folder}", new { type = "page", title = "Guides", version = new { number = 2 }, body = new { storage = new { value = "<p>fits</p>", representation = "storage" } } }, Ct))
            .JsonAsync(HttpStatusCode.OK);
    }
}
