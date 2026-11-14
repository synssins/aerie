using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Roles: one global list of named sets of the ten space permissions. Reader, Contributor, Supervisor and Owner are built in
/// (renamed and edited, never deleted); Admins add more. A person's permissions in a space are the union of their roles there.
/// A role that manages members and settings counts as Owner: only an Admin grants, changes or removes it.
/// </summary>
public sealed class RolesTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> SpaceAsync(string key) =>
        (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key, name = key }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return (c, id);
    }

    private static Task<HttpResponseMessage> GrantAsync(HttpClient by, Guid space, string type, string principal, Guid roleId) =>
        by.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = type, principalId = principal, roleId }, Ct);

    private async Task<List<JsonElement>> RolesAsync(HttpClient? by = null) =>
        [.. (await (by ?? _admin).GetFromJsonAsync<JsonElement>("/api/v1/roles", Ct)).EnumerateArray()];

    private async Task<Guid> RoleIdAsync(string name) => (await RolesAsync()).Single(r => r.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    private Task<HttpResponseMessage> CreateRoleAsync(string name, params string[] permissions) =>
        _admin.PostAsJsonAsync("/api/v1/admin/roles", new { name, permissions }, Ct);

    private async Task<Guid> NewRoleAsync(string name, params string[] permissions)
    {
        var r = await CreateRoleAsync(name, permissions);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> NewPageAsync(HttpClient by, Guid space, string title = "Page") =>
        by.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title, contentJson = Doc }, Ct);

    [Fact]
    public async Task BuiltIns_AreListed_WithTodaysPermissions_AndCannotBeDeleted()
    {
        var roles = await RolesAsync();
        string[] Perms(string n) => [.. roles.Single(r => r.GetProperty("name").GetString() == n).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
        Assert.Equal(["Read"], Perms("Reader"));
        Assert.Equal(["Read", "CreateEdit", "MoveDeleteOwn"], Perms("Contributor"));
        Assert.Equal(8, Perms("Supervisor").Length);
        Assert.Equal(10, Perms("Owner").Length);
        Assert.All(roles, r => Assert.NotEqual(JsonValueKind.Null, r.GetProperty("builtIn").ValueKind));
        var owner = await RoleIdAsync("Owner");
        var refused = await _admin.DeleteAsync($"/api/v1/admin/roles/{owner}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task ABuiltIn_IsRenamedAndEdited_AndThatIsWhatItsHoldersGet()
    {
        var space = await SpaceAsync("docs");
        var (cara, caraId) = await UserAsync("cara");
        var contributor = await RoleIdAsync("Contributor");
        (await GrantAsync(_admin, space, "User", caraId.ToString(), contributor)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, (await NewPageAsync(cara, space)).StatusCode);

        var r = await _admin.PutAsJsonAsync($"/api/v1/admin/roles/{contributor}", new { name = "Writer", permissions = new[] { "Read" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await NewPageAsync(cara, space)).StatusCode);
        Assert.Equal("Writer", (await RolesAsync()).Single(x => x.GetProperty("id").GetGuid() == contributor).GetProperty("name").GetString());
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RoleUpdate && a.TargetId == contributor.ToString(), Ct)));
    }

    [Fact]
    public async Task ACustomRole_GivesItsPermissions_AndRolesAddUp()
    {
        var space = await SpaceAsync("ops");
        var editor = await NewRoleAsync("Editor", "Read", "CreateEdit");
        var (eve, eveId) = await UserAsync("eve");
        // Reader directly, Editor through a group: together they may create, not delete.
        (await GrantAsync(_admin, space, "User", eveId.ToString(), await RoleIdAsync("Reader"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await NewPageAsync(eve, space)).StatusCode);
        var group = (await (await _admin.PostAsJsonAsync("/api/v1/admin/groups", new { name = "Editors" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct));
        (await _admin.PutAsync($"/api/v1/admin/groups/{group.GetProperty("id").GetGuid()}/members/{eveId}", null, Ct)).EnsureSuccessStatusCode();
        (await GrantAsync(_admin, space, "Group", group.GetProperty("key").GetString()!, editor)).EnsureSuccessStatusCode();

        var made = await NewPageAsync(eve, space);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var page = (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        // Editor has no "Move and delete own".
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        var row = (await GetRowsAsync(space)).Single(x => x.GetProperty("principalType").GetString() == "Group");
        Assert.Equal("Editor", row.GetProperty("roleName").GetString());
    }

    private async Task<List<JsonElement>> GetRowsAsync(Guid space) => [.. (await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{space}/roles", Ct)).EnumerateArray()];

    [Fact]
    public async Task ARoleThatManagesMembers_IsAnAdminsToGrantChangeOrRemove()
    {
        var space = await SpaceAsync("hr");
        var (olga, olgaId) = await UserAsync("olga");
        (await GrantAsync(_admin, space, "User", olgaId.ToString(), await RoleIdAsync("Owner"))).EnsureSuccessStatusCode();
        var steward = await NewRoleAsync("Steward", "Read", "ManageRolesSettingsSensitive");
        var helper = await NewRoleAsync("Helper", "Read", "CreateEdit");
        var (_, samId) = await UserAsync("sam");

        // The Owner gives a role below Owner, but not one that manages members.
        Assert.Equal(HttpStatusCode.Forbidden, (await GrantAsync(olga, space, "User", samId.ToString(), steward)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(olga, space, "User", samId.ToString(), helper)).StatusCode);
        // The Admin makes Sam a Steward; the Owner can neither change nor remove that row now.
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_admin, space, "User", samId.ToString(), steward)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GrantAsync(olga, space, "User", samId.ToString(), helper)).StatusCode);
        var samRow = (await GetRowsAsync(space)).Single(r => r.GetProperty("principalId").GetString() == samId.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.DeleteAsync($"/api/v1/spaces/{space}/roles/{samRow.GetProperty("id").GetGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/spaces/{space}/roles/{samRow.GetProperty("id").GetGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task ARoleInUse_IsNotDeleted_AndTheCountIsGiven()
    {
        var space = await SpaceAsync("net");
        var temp = await NewRoleAsync("Temp", "Read");
        var (_, id) = await UserAsync("tim");
        (await GrantAsync(_admin, space, "User", id.ToString(), temp)).EnsureSuccessStatusCode();
        var refused = await _admin.DeleteAsync($"/api/v1/admin/roles/{temp}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Used in 1 place.", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());

        var row = (await GetRowsAsync(space)).Single(r => r.GetProperty("principalId").GetString() == id.ToString());
        (await _admin.DeleteAsync($"/api/v1/spaces/{space}/roles/{row.GetProperty("id").GetGuid()}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/roles/{temp}", Ct)).StatusCode);
        Assert.DoesNotContain(await RolesAsync(), r => r.GetProperty("id").GetGuid() == temp);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RoleDelete && a.TargetId == temp.ToString(), Ct)));
    }

    [Fact]
    public async Task ARole_IsChecked_AndOnlyAnAdminChangesRoles()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRoleAsync("No read", "CreateEdit")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRoleAsync("Odd", "Read", "Fly")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRoleAsync(" ", "Read")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRoleAsync("reader", "Read")).StatusCode); // the name is taken
        var (user, _) = await UserAsync("una");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/roles", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/admin/roles", new { name = "Mine", permissions = new[] { "Read" } }, Ct)).StatusCode);
        var reader = await RoleIdAsync("Reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync($"/api/v1/admin/roles/{reader}", new { name = "Reader", permissions = new[] { "Read", "CreateEdit" } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/v1/admin/roles/{reader}", Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RoleCreate, Ct)));
    }

    [Fact]
    public async Task AWriteToken_IsCappedAtCreateAndMoveOwn()
    {
        var space = await SpaceAsync("tok");
        var boss = await NewRoleAsync("Boss", "Read", "CreateEdit", "MoveDeleteOwn", "MoveDeleteOthers");
        var (bea, beaId) = await UserAsync("bea");
        (await GrantAsync(_admin, space, "User", beaId.ToString(), boss)).EnsureSuccessStatusCode();
        var others = (await (await NewPageAsync(_admin, space, "Admin's")).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var made = await (await bea.PostAsJsonAsync("/api/v1/tokens", new { name = "t", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)space, access = "Write" } }, days = 1 }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = _f.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("token").GetString());
        var own = await NewPageAsync(token, space, "Mine");
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync($"/api/v1/pages/{others}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await token.DeleteAsync($"/api/v1/pages/{(await own.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid()}", Ct)).StatusCode);
        // Bea herself may delete others' pages.
        Assert.Equal(HttpStatusCode.NoContent, (await bea.DeleteAsync($"/api/v1/pages/{others}", Ct)).StatusCode);
    }

    [Fact]
    public async Task GrantingByTheOldRoleName_StillWorks()
    {
        var space = await SpaceAsync("old");
        var (_, id) = await UserAsync("ola");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = id.ToString(), role = "Contributor" }, Ct)).StatusCode);
        var row = (await GetRowsAsync(space)).Single(r => r.GetProperty("principalId").GetString() == id.ToString());
        Assert.Equal((await RoleIdAsync("Contributor")), row.GetProperty("roleId").GetGuid());
        Assert.Equal("Contributor", row.GetProperty("role").GetString());
    }

    /// <summary>The upgrade: every assignment moves to the built-in role of the same level.</summary>
    [Fact]
    public async Task Upgrade_MovesAssignmentsToTheBuiltInRoles()
    {
        var path = KbAppFactory.NewDbPath();
        var options = new DbContextOptionsBuilder<KbDbContext>().UseSqlite($"Data Source={path}", s => s.MigrationsAssembly(KbDbContext.SqliteMigrations)).Options;
        await using (var db = new KbDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("SpaceVisibility", Ct);
            for (var level = 1; level <= 4; level++)
                await db.Database.ExecuteSqlRawAsync("INSERT INTO SpaceRoleAssignments (Id, SpaceId, PrincipalType, PrincipalId, Role) VALUES ({0}, '11111111-1111-1111-1111-111111111111', 0, {1}, {2})",
                    [Guid.NewGuid().ToString().ToUpperInvariant(), $"user-{level}", level], Ct);
            await migrator.MigrateAsync(cancellationToken: Ct);
        }
        await using (var db = new KbDbContext(options))
        {
            var rows = await db.SpaceRoleAssignments.AsNoTracking().OrderBy(a => a.PrincipalId).ToListAsync(Ct);
            var roles = await db.SpaceRoleDefs.AsNoTracking().ToDictionaryAsync(r => r.Id, Ct);
            Assert.Equal(["Reader", "Contributor", "Supervisor", "Owner"], rows.Select(r => roles[r.RoleId].Name));
        }
    }
}
