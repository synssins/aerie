using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>
/// Permissions a plugin declares in its manifest: a space permission is held from a minimum role up, which an Admin can change;
/// a global permission is held by the people an Admin gave it to, and by Admins.
/// </summary>
public sealed class PermissionTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private const string See = "kb.test.probe:see", Run = "kb.test.probe:run";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space, _adminId, _reader, _supervisor, _owner, _gail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe, m => m with
        {
            Permissions = [new PluginPermission("see", "See notes", "space", "Supervisor"), new PluginPermission("run", "Run reports", "global")],
        }));
        await RestartAsync();
        _adminId = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        _reader = await UserAsync("rita", "Reader");
        _supervisor = await UserAsync("sue", "Supervisor");
        _owner = await UserAsync("olga", "Owner");
        _gail = await UserAsync("gail", null);
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    private async Task<Guid> UserAsync(string name, string? role)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, "user-password-0123456789")).Succeeded);
            id = user.Id;
        }
        if (role is not null)
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>What the plugin would be told for this person: asked of IAccess itself.</summary>
    private async Task<bool> CanAsync(Guid user, string permission, Guid? space = null)
    {
        await using var scope = _f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccess>().CanAsync(Actor.User(user), permission, space, Ct);
    }

    private Task<HttpResponseMessage> RolesAsync(object roles) => _admin.PutAsJsonAsync($"{Url}/kb.test.probe/permissions", new { roles }, Ct);
    private Task<HttpResponseMessage> GrantAsync(Guid user, string permission, bool granted) => _admin.PutAsJsonAsync($"/api/v1/admin/users/{user}/permissions", new { permission, granted }, Ct);

    [Fact]
    public async Task SpacePermission_HeldFromItsDefaultRoleUp()
    {
        Assert.False(await CanAsync(_reader, See, _space));
        Assert.True(await CanAsync(_supervisor, See, _space));
        Assert.True(await CanAsync(_owner, See, _space));
        Assert.True(await CanAsync(_adminId, See, _space)); // an Admin is Owner of a space that is not sensitive
        Assert.False(await CanAsync(_gail, See, _space));   // no role in the space
        Assert.False(await CanAsync(_supervisor, See));     // a space permission without a space is not held
    }

    [Fact]
    public async Task SpacePermission_AdminChangesTheMinimum()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await RolesAsync(new { see = "Owner" })).StatusCode);
        Assert.False(await CanAsync(_supervisor, See, _space));
        Assert.True(await CanAsync(_owner, See, _space));

        Assert.Equal(HttpStatusCode.NoContent, (await RolesAsync(new { see = "Reader" })).StatusCode);
        Assert.True(await CanAsync(_reader, See, _space));

        var listed = (await _admin.GetFromJsonAsync<JsonElement>($"{Url}/kb.test.probe/permissions", Ct)).GetProperty("permissions").EnumerateArray().ToList();
        Assert.Equal([("see", "See notes", "space", "Supervisor", "Reader"), ("run", "Run reports", "global", "Owner", null)],
            listed.Select(p => (p.GetProperty("key").GetString(), p.GetProperty("label").GetString(), p.GetProperty("scope").GetString(), p.GetProperty("defaultRole").GetString(), p.GetProperty("role").GetString())));

        Assert.Equal(HttpStatusCode.BadRequest, (await RolesAsync(new { see = "Emperor" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RolesAsync(new { nothing = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RolesAsync(new { run = "Owner" })).StatusCode); // a global permission has no role
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync($"{Url}/kb.nothing/permissions", new { roles = new { see = "Owner" } }, Ct)).StatusCode);
    }

    [Fact]
    public async Task GlobalPermission_HeldOnlyWhenGranted_OrAdmin()
    {
        Assert.False(await CanAsync(_gail, Run));
        Assert.False(await CanAsync(_owner, Run)); // a role in a space gives no global permission
        Assert.True(await CanAsync(_adminId, Run));

        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_gail, Run, true)).StatusCode);
        Assert.True(await CanAsync(_gail, Run));
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_gail, Run, true)).StatusCode); // twice is once
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_gail, Run, false)).StatusCode);
        Assert.False(await CanAsync(_gail, Run));

        Assert.Equal(HttpStatusCode.BadRequest, (await GrantAsync(_gail, "kb.test.probe:nothing", true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GrantAsync(_gail, See, true)).StatusCode); // a space permission comes from the role
        Assert.Equal(HttpStatusCode.NotFound, (await GrantAsync(Guid.NewGuid(), Run, true)).StatusCode);
    }

    [Fact]
    public async Task Permission_OfAPluginNotLoaded_OrUnknown_IsNotHeld()
    {
        Assert.False(await CanAsync(_adminId, "kb.test.probe:nothing", _space));
        Assert.False(await CanAsync(_adminId, "kb.nothing:see", _space));
        Assert.False(await CanAsync(_adminId, "nonsense", _space));
        await GrantAsync(_gail, Run, true);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"{Url}/kb.test.probe/state", new { enabled = false }, Ct)).StatusCode);
        await RestartAsync();

        Assert.False(await CanAsync(_supervisor, See, _space));
        Assert.False(await CanAsync(_adminId, Run));
        Assert.False(await CanAsync(_gail, Run)); // the grant is kept, and counts again when the plugin is back
    }

    [Fact]
    public async Task Me_AndTheUsersList_ShowHeldGlobalPermissions()
    {
        var gail = _f.NewClient();
        (await gail.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "gail", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        Assert.Empty((await gail.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("permissions").EnumerateArray());

        await GrantAsync(_gail, Run, true);

        Assert.Equal([Run], (await gail.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal([Run], (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString())); // an Admin holds every one
        var users = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Ct);
        Assert.Equal([Run], users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "gail").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Empty(users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "rita").GetProperty("permissions").EnumerateArray());
    }

    [Fact]
    public async Task PermissionChanges_AreAudited_AndNeedAnAdmin()
    {
        await RolesAsync(new { see = "Owner" });
        await GrantAsync(_gail, Run, true);

        await _f.WithDbAsync(async db =>
        {
            var role = await db.AuditEntries.SingleAsync(a => a.Action == "plugin.permissions.change", Ct);
            Assert.Equal(("plugin", "kb.test.probe", _adminId), (role.TargetType, role.TargetId, role.ActorId));
            Assert.Contains("\"see\":\"Owner\"", role.DataJson);
            var grant = await db.AuditEntries.SingleAsync(a => a.Action == "user.permissions.set", Ct);
            Assert.Equal(("user", _gail.ToString(), _adminId), (grant.TargetType, grant.TargetId, grant.ActorId));
            Assert.Contains(Run, grant.DataJson);
            Assert.Contains("\"granted\":\"True\"", grant.DataJson);
        });

        var olga = _f.NewClient();
        (await olga.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "olga", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.GetAsync($"{Url}/kb.test.probe/permissions", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.PutAsJsonAsync($"{Url}/kb.test.probe/permissions", new { roles = new { see = "Reader" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.PutAsJsonAsync($"/api/v1/admin/users/{_owner}/permissions", new { permission = Run, granted = true }, Ct)).StatusCode);
        Assert.False(await CanAsync(_owner, Run));
    }
}
