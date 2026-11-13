using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// The recycle bin is browsed like the knowledge base: a tree of what was deleted, and a deleted page opened to read. Whoever
/// manages a space's bin browses it. A deleted space lies in the admin bin with everything under it, for Admins only, and a
/// sensitive one shows nothing of what it holds.
/// </summary>
public sealed class BinBrowseTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"restart the spooler"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _sue = null!, _rita = null!, _otto = null!;
    private Guid _space;
    private PageDto _folder = null!, _page = null!, _kept = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        _sue = await UserAsync("sue", "Supervisor");
        _rita = await UserAsync("rita", "Reader");
        _otto = await UserAsync("otto", null);
        _folder = await MakeAsync("Folder", "Printers", null);
        _page = await MakeAsync("Page", "Spooler", _folder.Id);
        _kept = await MakeAsync("Page", "Kept", null);
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<PageDto> MakeAsync(string kind, string title, Guid? parentId) =>
        (await (await _sue.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind, title, parentId, contentJson = kind == "Page" ? Doc : null }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;

    private async Task<HttpClient> UserAsync(string name, string? role, bool admin = false)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, IsAdmin = admin, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        if (role is not null) (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<List<(Guid Id, Guid? Parent, Guid Space, string Kind, string Title)>> NodesAsync(HttpClient by, string path) =>
        [.. (await by.GetFromJsonAsync<JsonElement>(path, Ct)).EnumerateArray().Select(n => (n.GetProperty("id").GetGuid(),
            n.GetProperty("parentId").ValueKind == JsonValueKind.Null ? (Guid?)null : n.GetProperty("parentId").GetGuid(),
            n.GetProperty("spaceId").GetGuid(), n.GetProperty("kind").GetString()!, n.GetProperty("title").GetString()!)).OrderBy(n => n.Item5)];

    private Task<HttpResponseMessage> DeleteFolderAsync() => _sue.DeleteAsync($"/api/v1/pages/{_folder.Id}", Ct);

    [Fact]
    public async Task SpaceBin_IsATree_ForWhoManagesTheBin()
    {
        (await DeleteFolderAsync()).EnsureSuccessStatusCode();

        // The folder and the page in it, the page under the folder. What was not deleted is not in the bin.
        Assert.Equal([(_folder.Id, null, _space, "Folder", "Printers"), (_page.Id, _folder.Id, _space, "Page", "Spooler")], await NodesAsync(_sue, $"/api/v1/spaces/{_space}/bin/tree"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _rita.GetAsync($"/api/v1/spaces/{_space}/bin/tree", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _otto.GetAsync($"/api/v1/spaces/{_space}/bin/tree", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync($"/api/v1/spaces/{_space}/bin/tree", Ct)).StatusCode);
    }

    [Fact]
    public async Task DeletedPage_IsRead_ByWhoManagesTheBin()
    {
        (await DeleteFolderAsync()).EnsureSuccessStatusCode();

        var read = await _sue.GetFromJsonAsync<JsonElement>($"/api/v1/bin/pages/{_page.Id}", Ct);
        Assert.Equal(("Spooler", Doc, _folder.Id, "sue"), (read.GetProperty("title").GetString(), read.GetProperty("contentJson").GetString(), read.GetProperty("restoreId").GetGuid(), read.GetProperty("deletedBy").GetString()));
        Assert.Equal(["Service Desk", "Printers"], read.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
        // A folder is opened like a folder of the knowledge base: what is in it is listed.
        var folder = await _sue.GetFromJsonAsync<JsonElement>($"/api/v1/bin/pages/{_folder.Id}", Ct);
        Assert.Equal([(_page.Id, "Page", "Spooler")], folder.GetProperty("children").EnumerateArray().Select(c => (c.GetProperty("id").GetGuid(), c.GetProperty("kind").GetString()!, c.GetProperty("title").GetString()!)));
        Assert.Empty(read.GetProperty("children").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await _rita.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _otto.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        // The bin holds deleted pages only: a live page is read as a page, a page that never was is not there.
        Assert.Equal(HttpStatusCode.NotFound, (await _sue.GetAsync($"/api/v1/bin/pages/{_kept.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _sue.GetAsync($"/api/v1/bin/pages/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task EmptiedItems_AreTheAdminsOnly()
    {
        (await DeleteFolderAsync()).EnsureSuccessStatusCode();
        (await _sue.PostAsync($"/api/v1/spaces/{_space}/bin/empty", null, Ct)).EnsureSuccessStatusCode();

        Assert.Empty(await NodesAsync(_sue, $"/api/v1/spaces/{_space}/bin/tree"));
        Assert.Equal(HttpStatusCode.NotFound, (await _sue.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(2, (await NodesAsync(_admin, "/api/v1/admin/bin/tree")).Count);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _sue.GetAsync("/api/v1/admin/bin/tree", Ct)).StatusCode);
    }

    [Fact]
    public async Task DeletedSpace_LiesInTheAdminBin_WithEverythingUnderIt()
    {
        (await _admin.PostAsJsonAsync($"/api/v1/admin/spaces/{_space}/delete", new { confirm = "delete desk" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal([(_kept.Id, null, _space, "Page", "Kept"), (_folder.Id, null, _space, "Folder", "Printers"), (_page.Id, _folder.Id, _space, "Page", "Spooler")], await NodesAsync(_admin, "/api/v1/admin/bin/tree"));
        var read = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/bin/pages/{_page.Id}", Ct);
        Assert.Equal(("Spooler", Doc), (read.GetProperty("title").GetString(), read.GetProperty("contentJson").GetString()));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("restoreId").ValueKind); // the space comes back whole, not page by page
        var folder = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/bin/pages/{_folder.Id}", Ct);
        Assert.Equal([_page.Id], folder.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        // Its former Supervisor has nothing of it: the space is gone for her.
        Assert.Equal(HttpStatusCode.NotFound, (await _sue.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _sue.GetAsync($"/api/v1/spaces/{_space}/bin/tree", Ct)).StatusCode);
    }

    /// <summary>An Admin has nothing in a sensitive space, and deleting it gives nothing either: its name is in the bin, its content is not.</summary>
    [Fact]
    public async Task DeletedSensitiveSpace_ShowsNothingOfWhatItHolds()
    {
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        var adam = await UserAsync("adam", null, admin: true);
        (await adam.PostAsJsonAsync($"/api/v1/admin/spaces/{_space}/delete", new { confirm = "delete desk" }, Ct)).EnsureSuccessStatusCode();

        foreach (var who in new[] { adam, _admin })
        {
            Assert.Empty(await NodesAsync(who, "/api/v1/admin/bin/tree"));
            Assert.Equal(HttpStatusCode.NotFound, (await who.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        }
        Assert.Single((await adam.GetFromJsonAsync<JsonElement>("/api/v1/admin/spaces/deleted", Ct)).EnumerateArray());
    }

    /// <summary>The same in a live sensitive space: the admin bin tree leaves it out, as the admin bin list does.</summary>
    [Fact]
    public async Task SensitiveSpace_IsNotInTheAdminBinTree_WithoutARoleThere()
    {
        (await DeleteFolderAsync()).EnsureSuccessStatusCode();
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        var adam = await UserAsync("adam", null, admin: true);

        Assert.Empty(await NodesAsync(adam, "/api/v1/admin/bin/tree"));
        Assert.Equal(HttpStatusCode.NotFound, (await adam.GetAsync($"/api/v1/bin/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(2, (await NodesAsync(_sue, $"/api/v1/spaces/{_space}/bin/tree")).Count);
    }
}
