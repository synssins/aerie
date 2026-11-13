using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>Move, delete, restore and the bins go through the action bus: each answers with its action's name, a plugin can refuse a write, and the admin bin checks the caller.</summary>
public sealed class PageStructurePipelineTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private async Task<PageDto> CreatedAsync(string title, string kind = "Page") =>
        (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind, title }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;

    private async Task<List<BinItemDto>> BinAsync(string url) => (await _admin.GetFromJsonAsync<List<BinItemDto>>(url, Ct))!;

    /// <summary>A signed-in person who is not an Admin, with a role in the space.</summary>
    private async Task<HttpClient> UserAsync(string name, string role)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var client = _f.NewClient();
        (await client.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    [Fact]
    public async Task EveryPageStructureRoute_AnswersWithItsActionName()
    {
        var folder = await CreatedAsync("How-to", "Folder");
        var page = await CreatedAsync("Reset a password");

        var moved = await _admin.PostAsJsonAsync($"/api/v1/pages/{page.Id}/move", new { parentId = folder.Id }, Ct);
        Assert.Equal((HttpStatusCode.OK, "page.move"), (moved.StatusCode, Action(moved)));
        Assert.Equal(folder.Id, (await moved.Content.ReadFromJsonAsync<PageDto>(Ct))!.ParentId);
        var deleted = await _admin.DeleteAsync($"/api/v1/pages/{page.Id}", Ct);
        Assert.Equal((HttpStatusCode.NoContent, "page.delete"), (deleted.StatusCode, Action(deleted)));
        var bin = await _admin.GetAsync($"/api/v1/spaces/{_space}/bin", Ct);
        Assert.Equal((HttpStatusCode.OK, "bin.list"), (bin.StatusCode, Action(bin)));
        var item = Assert.Single((await bin.Content.ReadFromJsonAsync<List<BinItemDto>>(Ct))!);
        Assert.Equal((page.Id, _space, "Service Desk", "Page", "Reset a password", "Service Desk / How-to", 1, "Break-glass administrator", false),
            (item.Id, item.SpaceId, item.SpaceName, item.Kind, item.Title, item.Location, item.Items, item.DeletedBy, item.Hidden));

        var restored = await _admin.PostAsync($"/api/v1/pages/{page.Id}/restore", null, Ct);
        Assert.Equal((HttpStatusCode.OK, "page.restore"), (restored.StatusCode, Action(restored)));
        // The answer is what it was: the two ids, under these names.
        Assert.Equal($$"""{"id":"{{page.Id}}","spaceId":"{{_space}}"}""", await restored.Content.ReadAsStringAsync(Ct));

        (await _admin.DeleteAsync($"/api/v1/pages/{page.Id}", Ct)).EnsureSuccessStatusCode();
        var emptied = await _admin.PostAsync($"/api/v1/spaces/{_space}/bin/empty", null, Ct);
        Assert.Equal((HttpStatusCode.NoContent, "bin.empty"), (emptied.StatusCode, Action(emptied)));
        Assert.Empty(await BinAsync($"/api/v1/spaces/{_space}/bin"));
        var all = await _admin.GetAsync("/api/v1/admin/bin", Ct);
        Assert.Equal((HttpStatusCode.OK, "bin.list.all"), (all.StatusCode, Action(all)));
        Assert.True(Assert.Single((await all.Content.ReadFromJsonAsync<List<BinItemDto>>(Ct))!).Hidden);

        // A refusal by the core names the action too.
        var missing = await _admin.PostAsJsonAsync($"/api/v1/pages/{Guid.NewGuid()}/move", new { parentId = folder.Id }, Ct);
        Assert.Equal((HttpStatusCode.NotFound, "page.move"), (missing.StatusCode, Action(missing)));
        var intoItself = await _admin.PostAsJsonAsync($"/api/v1/pages/{folder.Id}/move", new { parentId = folder.Id }, Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "page.move"), (intoItself.StatusCode, Action(intoItself)));
        Assert.Equal("A folder can't move into itself.", (await intoItself.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("parentId")[0].GetString());

        // Each write is in the audit log once, under today's name and with today's data, done by the caller.
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        await _f.WithDbAsync(async db =>
        {
            var written = await db.AuditEntries.AsNoTracking().Where(a => a.SpaceId == _space && a.Action != AuditActions.SpaceCreate).OrderBy(a => a.Sequence)
                .Select(a => new { a.Action, a.ActorId, a.TargetType, a.TargetId, a.DataJson }).ToListAsync(Ct);
            Assert.Equal([AuditActions.PageMove, AuditActions.PageDelete, AuditActions.PageRestore, AuditActions.PageDelete, AuditActions.BinEmpty], written.Select(a => a.Action));
            Assert.All(written, a => Assert.Equal(me, a.ActorId));
            Assert.All(written.Take(4), a => Assert.Equal(("page", page.Id.ToString()), (a.TargetType, a.TargetId)));
            Assert.Equal(("space", _space.ToString()), (written[4].TargetType, written[4].TargetId));
            Assert.Equal([$$"""{"fromSpace":"{{_space}}","parent":"{{folder.Id}}"}""", """{"title":"Reset a password","items":"1"}""", """{"title":"Reset a password"}""",
                """{"title":"Reset a password","items":"1"}""", """{"items":"1"}"""], written.Select(a => a.DataJson));
        });
    }

    [Fact]
    public async Task Delete_RefusedByHook_Is422_PageStays()
    {
        var page = await CreatedAsync("Reset a password");
        await SetAsync("Refuse", "page.delete");

        var r = await _admin.DeleteAsync($"/api/v1/pages/{page.Id}", Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/pages/{page.Id}", Ct)).StatusCode);
        Assert.Empty(await BinAsync("/api/v1/admin/bin"));
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.PageDelete, Ct)));
    }

    [Fact]
    public async Task AdminBin_SignedInButNotAdmin_Is403()
    {
        var owner = await UserAsync("owner1", "Owner");

        // The handler refuses, not a filter in front of it: the answer names the action.
        var all = await owner.GetAsync("/api/v1/admin/bin", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "bin.list.all"), (all.StatusCode, Action(all)));
    }

    [Fact]
    public async Task Restore_ChecksTheCaller_AHiddenItemIsAnAdmins()
    {
        var page = await CreatedAsync("Reset a password");
        (await _admin.DeleteAsync($"/api/v1/pages/{page.Id}", Ct)).EnsureSuccessStatusCode();
        var contributor = await UserAsync("carol", "Contributor");
        var supervisor = await UserAsync("sue", "Supervisor");

        // Someone with a role that does not manage the bin: 403. Once the bin is emptied the item is the admin bin's: 404 for everyone else.
        var refused = await contributor.PostAsync($"/api/v1/pages/{page.Id}/restore", null, Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "page.restore"), (refused.StatusCode, Action(refused)));
        (await supervisor.PostAsync($"/api/v1/spaces/{_space}/bin/empty", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await supervisor.PostAsync($"/api/v1/pages/{page.Id}/restore", null, Ct)).StatusCode);
        // A page that is not deleted is not there to restore.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"/api/v1/pages/{(await CreatedAsync("Live")).Id}/restore", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/pages/{page.Id}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/pages/{page.Id}", Ct)).StatusCode);
    }
}
