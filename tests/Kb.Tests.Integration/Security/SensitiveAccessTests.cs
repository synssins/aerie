using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Access;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Findings M10, M12 and M13 of the code review of 2026-11-17: the Owners of a sensitive space hear of it when an Admin gives someone a
/// role there by way of a group or makes a password link for one of its members; an access request cannot make an Owner where only an
/// Admin may, and is bounded in size and number; emergency access is never more than reading, whatever the Reader role holds.
/// </summary>
public sealed class SensitiveAccessTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"payroll run"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _olga = null!;
    private Guid _secret, _open, _olgaId, _group;
    private string _groupKey = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _secret = await SpaceAsync("Payroll");
        _open = await SpaceAsync("Desk");
        (_olga, _olgaId) = await UserAsync("olga");
        var group = await (await _admin.PostAsJsonAsync("/api/v1/admin/groups", new { name = "Payroll readers" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        (_group, _groupKey) = (group.GetProperty("id").GetGuid(), group.GetProperty("key").GetString()!);
        foreach (var space in new[] { _secret, _open })
        {
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = _olgaId.ToString(), role = "Owner" }, Ct)).EnsureSuccessStatusCode();
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "Group", principalId = _groupKey, role = "Reader" }, Ct)).EnsureSuccessStatusCode();
        }
        Assert.Equal(HttpStatusCode.Created, (await _olga.PostAsJsonAsync($"/api/v1/spaces/{_secret}/pages", new { kind = "Page", title = "Run", contentJson = Doc }, Ct)).StatusCode);
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_secret}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> SpaceAsync(string name) =>
        (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name + " Person", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return (c, id);
    }

    private async Task<List<string>> AlertsAsync(Guid user)
    {
        var titles = new List<string>();
        await _f.WithDbAsync(async db => titles = await db.Notifications.Where(n => n.UserId == user && n.Kind == NotificationKinds.SecurityAlert).Select(n => n.Title).ToListAsync(Ct));
        return titles;
    }

    // ---- M10 ----

    [Fact]
    public async Task AddingSomeoneToAGroupWithARoleInASensitiveSpace_AlertsItsOwners()
    {
        var (_, eve) = await UserAsync("eve");
        var before = (await AlertsAsync(_olgaId)).Count;
        (await _admin.PutAsync($"/api/v1/admin/groups/{_group}/members/{eve}", null, Ct)).EnsureSuccessStatusCode();
        var alerts = (await AlertsAsync(_olgaId)).Skip(before).ToList();
        // One alert, for the sensitive space: the open one has no such rule.
        Assert.Equal(["eve Person was added to a group that has access to Payroll"], alerts);
        // Adding the same person again changes nothing and says nothing.
        (await _admin.PutAsync($"/api/v1/admin/groups/{_group}/members/{eve}", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(before + 1, (await AlertsAsync(_olgaId)).Count);
    }

    [Fact]
    public async Task APasswordLinkForAMemberOfASensitiveSpace_AlertsItsOwners()
    {
        var (_, outsider) = await UserAsync("oscar");
        var before = (await AlertsAsync(_olgaId)).Count;
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/admin/users/{outsider}/password-link", null, Ct)).StatusCode);
        Assert.Equal(before, (await AlertsAsync(_olgaId)).Count); // not a member: nothing to tell

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/admin/users/{_olgaId}/password-link", null, Ct)).StatusCode);
        Assert.Equal(["A password link was made for olga Person, a member of Payroll"], (await AlertsAsync(_olgaId)).Skip(before));
    }

    // ---- M12 ----

    [Fact]
    public async Task AccessRequest_CannotAskForOwner_WhereOnlyAnAdminMakesOne()
    {
        var (bob, bobId) = await UserAsync("bob");
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync($"/api/v1/spaces/{_open}/access-requests", new { role = "Owner", reason = "please" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync($"/api/v1/spaces/{_open}/access-requests", new { role = 99, reason = "please" }, Ct)).StatusCode);
        // A sensitive space has no Admin to ask: its Owners make Owners, as they do when they grant a role.
        Assert.True((await bob.PostAsJsonAsync($"/api/v1/spaces/{_secret}/access-requests", new { role = "Owner", reason = "please" }, Ct)).IsSuccessStatusCode);

        // A request for Owner that was stored before this rule cannot be approved by an Owner who is not an Admin.
        var old = new AccessRequest { SpaceId = _open, RequesterId = bobId, RequestedRole = SpaceRole.Owner, Status = AccessRequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        await _f.WithDbAsync(async db => { db.AccessRequests.Add(old); await db.SaveChangesAsync(Ct); });
        Assert.Equal(HttpStatusCode.Forbidden, (await _olga.PostAsJsonAsync($"/api/v1/access-requests/{old.Id}/decision", new { approve = true }, Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.False(await db.SpaceRoleAssignments.AnyAsync(a => a.SpaceId == _open && a.PrincipalId == bobId.ToString(), Ct)));
    }

    [Fact]
    public async Task AccessRequest_IsBoundedInSizeAndNumber()
    {
        var (bob, _) = await UserAsync("bob");
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync($"/api/v1/spaces/{_open}/access-requests", new { role = "Reader", reason = new string('x', 281) }, Ct)).StatusCode);
        Assert.True((await bob.PostAsJsonAsync($"/api/v1/spaces/{_open}/access-requests", new { role = "Reader", reason = new string('x', 280) }, Ct)).IsSuccessStatusCode);
        // One waiting request per person and space.
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync($"/api/v1/spaces/{_open}/access-requests", new { role = "Contributor", reason = "again" }, Ct)).StatusCode);
        Assert.True((await bob.PostAsJsonAsync($"/api/v1/spaces/{_secret}/access-requests", new { role = "Reader", reason = "another space" }, Ct)).IsSuccessStatusCode);
        await _f.WithDbAsync(async db => Assert.Equal(2, await db.AccessRequests.CountAsync(Ct)));
    }

    // ---- M13 ----

    [Fact]
    public async Task EmergencyAccess_IsReading_WhateverTheReaderRoleHoldsNow()
    {
        // The Admin puts editing and managing members on the built-in Reader role, then opens emergency access.
        var roles = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/roles", Ct);
        var reader = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Reader");
        var all = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Owner").GetProperty("permissions");
        (await _admin.PutAsJsonAsync($"/api/v1/admin/roles/{reader.GetProperty("id").GetGuid()}", new { name = "Reader", permissions = all }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/v1/spaces/{_secret}", Ct)).StatusCode);
        Assert.True((await _admin.PostAsJsonAsync($"/api/v1/spaces/{_secret}/emergency-access", new { reason = "Payroll run is stuck" }, Ct)).IsSuccessStatusCode);

        // Reading: yes. Writing and making oneself an Owner: no.
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/spaces/{_secret}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_secret}/pages", new { kind = "Page", title = "Mine", contentJson = Doc }, Ct)).StatusCode);
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_secret}/roles", new { principalType = "User", principalId = me.ToString(), role = "Owner" }, Ct)).StatusCode);
    }
}
