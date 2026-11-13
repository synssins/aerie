using System.Net;
using System.Net.Http.Json;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Web;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

public sealed class PageTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<(HttpClient Admin, SpaceDto Space)> SpaceAsync()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var s = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        return (admin, s!);
    }

    private static async Task<PageDto> CreateAsync(HttpClient c, Guid spaceId, string title, string kind = "Page", Guid? parentId = null)
    {
        var r = await c.PostAsJsonAsync($"/api/v1/spaces/{spaceId}/pages", new { parentId, kind, title }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient c, Guid id, string baseVersion, bool major = false, string content = Doc) =>
        c.PutAsJsonAsync($"/api/v1/pages/{id}", new { title = "Reset password", contentJson = content, baseVersion, major }, Ct);

    /// <summary>A signed-in local user with an optional role in the space.</summary>
    private async Task<HttpClient> UserAsync(string name, Guid spaceId, SpaceRole? role)
    {
        Guid id = default;
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
    public async Task CreatePage_StartsAt1_0_AndAppearsInTree()
    {
        var (admin, s) = await SpaceAsync();
        var p = await CreateAsync(admin, s.Id, "Reset password");
        Assert.Equal("1.0", p.Version);
        var tree = await admin.GetFromJsonAsync<List<TreeNodeDto>>($"/api/v1/spaces/{s.Id}/tree", Ct);
        Assert.Contains(tree!, n => n.Id == p.Id && n.Kind == "Page" && n.Title == "Reset password");
    }

    [Fact]
    public async Task Save_AddsPointOne_1_9_Then_1_10_AndMajorGoesToNextWhole()
    {
        var (admin, s) = await SpaceAsync();
        var p = await CreateAsync(admin, s.Id, "Reset password");
        var v = p.Version;
        for (var i = 0; i < 10; i++)
            v = (await (await SaveAsync(admin, p.Id, v)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Version;
        Assert.Equal("1.10", v);
        var major = await (await SaveAsync(admin, p.Id, v, major: true)).Content.ReadFromJsonAsync<PageDto>(Ct);
        Assert.Equal("2.0", major!.Version);
        Assert.Equal(Doc, major.ContentJson);
        Assert.Equal("2.0", major.History[0].Version);
        Assert.Equal("Break-glass administrator", major.History[0].Author);
    }

    [Fact]
    public async Task Save_WithStaleVersion_Returns409()
    {
        var (admin, s) = await SpaceAsync();
        var p = await CreateAsync(admin, s.Id, "Reset password");
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(admin, p.Id, "1.0")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SaveAsync(admin, p.Id, "1.0")).StatusCode);
    }

    [Fact]
    public async Task Save_InvalidJson_Returns400()
    {
        var (admin, s) = await SpaceAsync();
        var p = await CreateAsync(admin, s.Id, "Reset password");
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(admin, p.Id, "1.0", content: "<script>")).StatusCode);
    }

    [Fact]
    public async Task Reader_CanView_ButNotCreateOrEdit()
    {
        var (admin, s) = await SpaceAsync();
        var p = await CreateAsync(admin, s.Id, "Reset password");
        var reader = await UserAsync("reader1", s.Id, SpaceRole.Reader);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/pages/{p.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/spaces/{s.Id}/pages", new { kind = "Page", title = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(reader, p.Id, "1.0")).StatusCode);
    }

    [Fact]
    public async Task NoRole_Gets404_ForTreeAndPage()
    {
        var (admin, s) = await SpaceAsync();
        var p = await CreateAsync(admin, s.Id, "Reset password");
        var stranger = await UserAsync("stranger1", s.Id, null);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/spaces/{s.Id}/tree", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/pages/{p.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Folders_HoldPages_AndPathListsAncestors()
    {
        var (admin, s) = await SpaceAsync();
        var f1 = await CreateAsync(admin, s.Id, "How-to", "Folder");
        var f2 = await CreateAsync(admin, s.Id, "Accounts", "Folder", f1.Id);
        var p = await CreateAsync(admin, s.Id, "Reset password", "Page", f2.Id);
        var got = await admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{p.Id}", Ct);
        Assert.Equal(["How-to", "Accounts"], got!.Path.Select(c => c.Title));

        // Only folders hold children.
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{s.Id}/pages", new { parentId = p.Id, kind = "Page", title = "child" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task SpaceRename_IsAdminOnly_EvenForOwners()
    {
        var (admin, s) = await SpaceAsync();
        var owner = await UserAsync("owner1", s.Id, SpaceRole.Owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}", new { name = "Renamed" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}", new { description = "Desk" }, Ct)).StatusCode);
        var r = await (await admin.PatchAsJsonAsync($"/api/v1/spaces/{s.Id}", new { name = "Renamed" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        Assert.Equal("Renamed", r!.Name);
    }

    [Fact]
    public async Task Tags_AreTrimmedDeduped_OnCreate_AndReplacedOnSave()
    {
        var (admin, s) = await SpaceAsync();
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{s.Id}/pages", new { kind = "Page", title = "VPN", tags = new[] { " vpn ", "VPN", "network" } }, Ct);
        var p = (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;
        Assert.Equal(["network", "vpn"], p.Tags.Order());

        var saved = await (await admin.PutAsJsonAsync($"/api/v1/pages/{p.Id}", new { title = "VPN", contentJson = Doc, baseVersion = "1.0", tags = new[] { "remote" } }, Ct))
            .Content.ReadFromJsonAsync<PageDto>(Ct);
        Assert.Equal(["remote"], saved!.Tags);
    }

    [Fact]
    public async Task Tags_TooLong_Returns400()
    {
        var (admin, s) = await SpaceAsync();
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{s.Id}/pages", new { kind = "Page", title = "x", tags = new[] { new string('a', 51) } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task CreateSpace_WithoutKey_GeneratesUniqueKey()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var a = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        var b = await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct);
        Assert.Equal("service-desk", a!.Key);
        Assert.NotEqual(a.Key, b!.Key);
    }

    [Fact]
    public async Task CreatePage_WithContent_FirstSaveIs1_0_WithOneHistoryEntry()
    {
        var (admin, s) = await SpaceAsync();
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{s.Id}/pages", new { kind = "Page", title = "User lookup", contentJson = Doc }, Ct);
        var p = (await r.Content.ReadFromJsonAsync<PageDto>(Ct))!;
        Assert.Equal("1.0", p.Version);
        Assert.Equal(Doc, p.ContentJson);
        Assert.Single(p.History);
    }

    [Fact]
    public async Task CreatePage_InvalidContent_Returns400()
    {
        var (admin, s) = await SpaceAsync();
        var r = await admin.PostAsJsonAsync($"/api/v1/spaces/{s.Id}/pages", new { kind = "Page", title = "x", contentJson = "nope" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
