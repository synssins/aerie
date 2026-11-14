using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Who may hold which role in a space, and local groups. An Admin makes Owners; an Owner gives every other role and never touches an
/// Owner row, its own included. A local group gets a role in a space and every member holds it. A directory group's members come
/// from the directory: Aerie does not change them.
/// </summary>
public sealed class SpaceMembersTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _owner = null!, _reader = null!;
    private Guid _space, _ownerId, _readerId, _otherId;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        (_owner, _ownerId) = await UserAsync("olga", "Owner");
        (_reader, _readerId) = await UserAsync("rita", "Reader");
        _otherId = await NewUserAsync("otto");
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> NewUserAsync(string name)
    {
        await using var scope = _f.Services.CreateAsyncScope();
        var user = new KbUser { UserName = name, DisplayName = name.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private async Task<(HttpClient, Guid)> UserAsync(string name, string role)
    {
        var id = await NewUserAsync(name);
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_admin, "User", id.ToString(), role)).StatusCode);
        var c = _f.NewClient();
        (await Codes.SignInAsync(_f, c, name, Password)).EnsureSuccessStatusCode();
        return (c, id);
    }

    private Task<HttpResponseMessage> GrantAsync(HttpClient by, string type, string id, string role) =>
        by.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = type, principalId = id, role }, Ct);

    private async Task<List<JsonElement>> RolesAsync() => [.. (await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{_space}/roles", Ct)).EnumerateArray()];

    private async Task<Guid> AssignmentOfAsync(string principalId) =>
        (await RolesAsync()).Single(a => a.GetProperty("principalId").GetString() == principalId).GetProperty("id").GetGuid();

    private async Task<string?> RoleOfAsync(string principalId) =>
        (await RolesAsync()).SingleOrDefault(a => a.GetProperty("principalId").GetString() == principalId) is { ValueKind: JsonValueKind.Object } a ? a.GetProperty("role").GetString() : null;

    [Fact]
    public async Task OnlyAnAdmin_MakesAnOwner()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await GrantAsync(_owner, "User", _otherId.ToString(), "Owner")).StatusCode);
        Assert.Null(await RoleOfAsync(_otherId.ToString()));
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_admin, "User", _otherId.ToString(), "Owner")).StatusCode);
        Assert.Equal("Owner", await RoleOfAsync(_otherId.ToString()));
    }

    [Fact]
    public async Task AnOwner_CannotRemoveOrLower_AnOwnerRow_ItsOwnIncluded()
    {
        var own = await AssignmentOfAsync(_ownerId.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _owner.DeleteAsync($"/api/v1/spaces/{_space}/roles/{own}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GrantAsync(_owner, "User", _ownerId.ToString(), "Reader")).StatusCode);
        Assert.Equal("Owner", await RoleOfAsync(_ownerId.ToString()));
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/spaces/{_space}/roles/{own}", Ct)).StatusCode);
    }

    [Fact]
    public async Task AnOwner_GivesAndChangesTheOtherRoles()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_owner, "User", _otherId.ToString(), "Supervisor")).StatusCode);
        Assert.Equal("Supervisor", await RoleOfAsync(_otherId.ToString()));
        // A grant sets the role: lowering one is a grant too.
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_owner, "User", _otherId.ToString(), "Reader")).StatusCode);
        Assert.Equal("Reader", await RoleOfAsync(_otherId.ToString()));
        var row = await AssignmentOfAsync(_otherId.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/spaces/{_space}/roles/{row}", Ct)).StatusCode);
        Assert.Null(await RoleOfAsync(_otherId.ToString()));
    }

    [Fact]
    public async Task Roles_AreListedWithNames()
    {
        var rita = (await RolesAsync()).Single(a => a.GetProperty("principalId").GetString() == _readerId.ToString());
        Assert.Equal("RITA", rita.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ALocalGroup_GivesItsRoleToEveryMember_AndTakesItBack()
    {
        var group = await (await _admin.PostAsJsonAsync("/api/v1/admin/groups", new { name = "Desk Team" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var (groupId, key) = (group.GetProperty("id").GetGuid(), group.GetProperty("key").GetString()!);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsync($"/api/v1/admin/groups/{groupId}/members/{_otherId}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_owner, "Group", key, "Contributor")).StatusCode);
        Assert.Equal("Desk Team", (await RolesAsync()).Single(a => a.GetProperty("principalId").GetString() == key).GetProperty("name").GetString());

        var otto = _f.NewClient();
        (await otto.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "otto", password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("Contributor", (await otto.GetFromJsonAsync<JsonElement>($"/api/v1/spaces/{_space}", Ct)).GetProperty("myRole").GetString());

        var users = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Ct);
        Assert.Contains("Desk Team", users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "otto").GetProperty("groups").EnumerateArray().Select(g => g.GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/groups/{groupId}/members/{_otherId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otto.GetAsync($"/api/v1/spaces/{_space}", Ct)).StatusCode);
    }

    [Fact]
    public async Task DeletingAGroup_TakesItsRolesWithIt()
    {
        var group = await (await _admin.PostAsJsonAsync("/api/v1/admin/groups", new { name = "Gone" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var key = group.GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await GrantAsync(_admin, "Group", key, "Reader")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/groups/{group.GetProperty("id").GetGuid()}", Ct)).StatusCode);
        Assert.Null(await RoleOfAsync(key));
    }

    [Fact]
    public async Task AGrant_ToAGroupThatDoesNotExist_IsRefused() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await GrantAsync(_owner, "Group", "local:" + Guid.NewGuid(), "Reader")).StatusCode);

    [Fact]
    public async Task ADirectoryGroup_KeepsTheMembersTheDirectoryGave()
    {
        var group = new KbGroup { Key = "ad-0001", Name = "KB-DESK-Reader-P", IsLocal = false };
        await _f.WithDbAsync(async db => { db.KbGroups.Add(group); await db.SaveChangesAsync(Ct); });
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PutAsync($"/api/v1/admin/groups/{group.Id}/members/{_otherId}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.DeleteAsync($"/api/v1/admin/groups/{group.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task People_AreFoundByOwnersAndAdmins_Only()
    {
        await _admin.PostAsJsonAsync("/api/v1/admin/groups", new { name = "Ottoman Team" }, Ct);
        var found = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/principals?q=ott", Ct);
        Assert.Contains(found.EnumerateArray(), p => p.GetProperty("type").GetString() == "User" && p.GetProperty("id").GetString() == _otherId.ToString());
        Assert.Contains(found.EnumerateArray(), p => p.GetProperty("type").GetString() == "Group" && p.GetProperty("name").GetString() == "Ottoman Team");
        Assert.Equal(HttpStatusCode.Forbidden, (await _reader.GetAsync("/api/v1/principals?q=ott", Ct)).StatusCode);
    }
}
