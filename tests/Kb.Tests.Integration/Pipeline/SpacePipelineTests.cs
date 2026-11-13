using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The space routes go through the action bus: each answers with its action's name, a plugin can refuse and shape them, and the admin ones check the caller.</summary>
public sealed class SpacePipelineTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private SpaceDto _space = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    /// <summary>A signed-in person who is not an Admin, and their id.</summary>
    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        var client = _f.NewClient();
        (await client.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return (client, id);
    }

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    [Fact]
    public async Task EverySpaceRoute_AnswersWithItsActionName()
    {
        var (_, tech) = await UserAsync("tech1");
        var created = await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Networks" }, Ct);
        Assert.Equal((HttpStatusCode.Created, "space.create"), (created.StatusCode, Action(created)));
        var id = (await created.Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        Assert.Equal($"/api/v1/spaces/{id}", created.Headers.Location?.ToString());

        Assert.Equal("space.list", Action(await _admin.GetAsync("/api/v1/spaces", Ct)));
        Assert.Equal("space.read", Action(await _admin.GetAsync($"/api/v1/spaces/{id}", Ct)));
        var updated = await _admin.PatchAsJsonAsync($"/api/v1/spaces/{id}", new { description = "Switches" }, Ct);
        Assert.Equal((HttpStatusCode.OK, "space.update"), (updated.StatusCode, Action(updated)));
        var byAdmin = await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{id}", new { name = "Network" }, Ct);
        Assert.Equal((HttpStatusCode.OK, "space.update.admin"), (byAdmin.StatusCode, Action(byAdmin)));
        Assert.Equal("space.list.all", Action(await _admin.GetAsync("/api/v1/admin/spaces", Ct)));

        var granted = await _admin.PutAsJsonAsync($"/api/v1/spaces/{id}/roles", new { principalType = "User", principalId = tech.ToString(), role = "Reader" }, Ct);
        Assert.Equal((HttpStatusCode.NoContent, "space.role.grant"), (granted.StatusCode, Action(granted)));
        var roles = await _admin.GetAsync($"/api/v1/spaces/{id}/roles", Ct);
        Assert.Equal((HttpStatusCode.OK, "space.role.list"), (roles.StatusCode, Action(roles)));
        var assignment = Assert.Single((await roles.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList());
        Assert.Equal(("User", tech.ToString(), "Reader"), (assignment.GetProperty("principalType").GetString(), assignment.GetProperty("principalId").GetString(), assignment.GetProperty("role").GetString()));
        var revoked = await _admin.DeleteAsync($"/api/v1/spaces/{id}/roles/{assignment.GetProperty("id").GetString()}", Ct);
        Assert.Equal((HttpStatusCode.NoContent, "space.role.revoke"), (revoked.StatusCode, Action(revoked)));

        // A refusal by the core names the action too.
        var missing = await _admin.GetAsync($"/api/v1/spaces/{Guid.NewGuid()}", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "space.read"), (missing.StatusCode, Action(missing)));

        // Each write is in the audit log once, under today's name, done by the caller.
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        await _f.WithDbAsync(async db =>
        {
            var written = await db.AuditEntries.AsNoTracking().Where(a => a.SpaceId == id).OrderBy(a => a.Sequence).Select(a => new { a.Action, a.ActorId, a.TargetType, a.TargetId }).ToListAsync(Ct);
            Assert.Equal([AuditActions.SpaceCreate, AuditActions.SpaceUpdate, AuditActions.SpaceUpdate, AuditActions.SpaceRoleGrant, AuditActions.SpaceRoleRevoke], written.Select(a => a.Action));
            Assert.All(written, a => Assert.Equal(me, a.ActorId));
            Assert.Equal(("User", tech.ToString()), (written[3].TargetType, written[3].TargetId));
        });
    }

    [Fact]
    public async Task Update_RefusedByHook_Is422_SpaceUnchanged()
    {
        await SetAsync("Refuse", "space.update");

        var r = await _admin.PatchAsJsonAsync($"/api/v1/spaces/{_space.Id}", new { description = "Changed", agingDays = 30 }, Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        var after = await _admin.GetFromJsonAsync<SpaceDto>($"/api/v1/spaces/{_space.Id}", Ct);
        Assert.Equal((null, 180), (after!.Description, after.AgingDays));
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SpaceUpdate, Ct)));
    }

    [Fact]
    public async Task Read_FilterTrimsTheResult()
    {
        await SetAsync("Blank", "space.read:Name");

        Assert.Equal("", (await _admin.GetFromJsonAsync<SpaceDto>($"/api/v1/spaces/{_space.Id}", Ct))!.Name);
        // The filter shapes the answer, not the space: the list, another read, still has the name.
        var list = await _admin.GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", Ct);
        Assert.Equal("Service Desk", Assert.Single(list!).Name);
    }

    [Fact]
    public async Task AdminRoutes_SignedInButNotAdmin_Are403()
    {
        var (user, id) = await UserAsync("owner1");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space.Id}/roles", new { principalType = "User", principalId = id.ToString(), role = "Owner" }, Ct)).EnsureSuccessStatusCode();

        var list = await user.GetAsync("/api/v1/admin/spaces", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "space.list.all"), (list.StatusCode, Action(list)));
        var update = await user.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space.Id}", new { description = "Changed" }, Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "space.update.admin"), (update.StatusCode, Action(update)));
        // 403 before 404: a space that does not exist is not told apart.
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PatchAsJsonAsync($"/api/v1/admin/spaces/{Guid.NewGuid()}", new { description = "Changed" }, Ct)).StatusCode);
        // Creating a space is an Admin's too, though its route is not under /admin.
        var create = await user.PostAsJsonAsync("/api/v1/spaces", new { name = "Mine" }, Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "space.create"), (create.StatusCode, Action(create)));
        // An Owner who is not an Admin changes the settings of the space, not its name.
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PatchAsJsonAsync($"/api/v1/spaces/{_space.Id}", new { name = "Renamed" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.PatchAsJsonAsync($"/api/v1/spaces/{_space.Id}", new { description = "Desk" }, Ct)).StatusCode);
        var after = await _admin.GetFromJsonAsync<SpaceDto>($"/api/v1/spaces/{_space.Id}", Ct);
        Assert.Equal(("Service Desk", "Desk"), (after!.Name, after.Description));
    }
}
