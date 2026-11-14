using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Local users, as an Admin manages them: add one (the new person sets their own password with a link that works once),
/// give an existing one a new link, and delete one. A deleted account is closed at once and its history stays readable.
/// </summary>
public sealed class UserAdminTests : IAsyncLifetime
{
    private const string Password = "a-long-enough-password-1";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<(Guid Id, string Token)> InviteAsync(string name, bool admin = false, HttpClient? by = null)
    {
        var r = await (by ?? _admin).PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = name, displayName = char.ToUpperInvariant(name[0]) + name[1..], isAdmin = admin }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var made = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (made.GetProperty("id").GetGuid(), made.GetProperty("setPasswordToken").GetString()!);
    }

    private Task<HttpResponseMessage> SetPasswordAsync(Guid user, string token, string password) =>
        _f.NewClient().PostAsJsonAsync("/api/v1/auth/local/password", new { userId = user, token, password }, Ct);

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient c, string name, string password = Password) =>
        await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password }, Ct);

    /// <summary>A local user with a password, signed in.</summary>
    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name, bool admin = false)
    {
        var (id, token) = await InviteAsync(name, admin);
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(id, token, Password)).StatusCode);
        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await Codes.SignInAsync(_f, c, name, Password)).StatusCode);
        return (c, id);
    }

    private async Task<List<string>> AuditAsync(string action)
    {
        List<string> rows = [];
        await _f.WithDbAsync(async db => rows = [.. (await db.AuditEntries.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Sequence).ToListAsync(Ct)).Select(a => $"{a.TargetId} {a.DataJson}")]);
        return rows;
    }

    /// <summary>A request that read the account before the delete and writes it after must not write the old row back.</summary>
    [Fact]
    public async Task Delete_IsNotUndone_ByAWriteThatReadTheAccountBefore()
    {
        var (_, id) = await UserAsync("erin");
        await using var scope = _f.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
        var stale = (await users.FindByIdAsync(id.ToString()))!;

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/users/{id}", Ct)).StatusCode);
        stale.DisplayName = "Erin again";
        var written = await users.UpdateAsync(stale);

        Assert.False(written.Succeeded);
        await _f.WithDbAsync(async db => Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct)) is { DeletedAt: not null, PasswordHash: null }));
    }

    [Fact]
    public async Task TwoAdmins_WhoDeleteEachOtherAtOnce_LeaveOne()
    {
        var (ann, annId) = await UserAsync("ann", admin: true);
        var (bob, bobId) = await UserAsync("bob", admin: true);

        var codes = await Task.WhenAll(ann.DeleteAsync($"/api/v1/admin/users/{bobId}", Ct), bob.DeleteAsync($"/api/v1/admin/users/{annId}", Ct));

        Assert.Single(codes, c => c.StatusCode == HttpStatusCode.NoContent);
        await _f.WithDbAsync(async db => Assert.Equal(1, await db.Users.CountAsync(u => u.IsAdmin && !u.IsBreakGlass && u.DeactivatedAt == null, Ct)));
    }

    /// <summary>Sign-in attempts are counted by where they come from, however the address is written and whatever else the request carries.</summary>
    [Fact]
    public async Task SignInLimit_HoldsWithATrailingSlash_AndWithARefusedToken()
    {
        await using var strict = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Kb:Auth:SignInsPerMinutePerAddress"] = "4" });
        var admin = await strict.SignedInBreakGlassAsync();
        var made = await (await admin.PostAsJsonAsync("/api/v1/tokens", new { name = "old", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 1 }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tokens/{made.GetProperty("info").GetProperty("id").GetString()}", Ct)).StatusCode);
        var revoked = strict.NewClient();
        revoked.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("token").GetString());
        var plain = strict.NewClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) codes.Add((await plain.PostAsJsonAsync("/api/v1/auth/local/signin/", new { userName = "nobody", password = "x" }, Ct)).StatusCode);
        for (var i = 0; i < 4; i++) codes.Add((await revoked.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "nobody", password = "x" }, Ct)).StatusCode);
        for (var i = 0; i < 2; i++) codes.Add((await plain.PostAsJsonAsync("/api/v1/auth/local/password/", new { userId = Guid.NewGuid(), token = "x", password = Password }, Ct)).StatusCode);

        // The window allowed four, the Admin's own sign-in among them: at most three of these ten were let through.
        Assert.True(codes.Count(c => c == HttpStatusCode.TooManyRequests) >= 7, string.Join(",", codes.Select(c => (int)c)));
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("detail", out var d) ? d.GetString() : null;

    [Fact]
    public async Task NewUser_SetsTheirOwnPassword_WithALinkThatWorksOnce()
    {
        var (id, token) = await InviteAsync("gail");
        var listed = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Ct)).EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "gail");
        Assert.Equal(("Gail", "Local", false), (listed.GetProperty("displayName").GetString(), listed.GetProperty("source").GetString(), listed.GetProperty("isAdmin").GetBoolean()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(_f.NewClient(), "gail")).StatusCode); // no password yet

        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(id, token, Password)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(_f.NewClient(), "gail")).StatusCode);
        // The link is used up: nobody who finds it later can take the account.
        var again = await SetPasswordAsync(id, token, "another-long-password-2");
        Assert.Equal((HttpStatusCode.BadRequest, "The link is not valid."), (again.StatusCode, await DetailAsync(again)));
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(_f.NewClient(), "gail")).StatusCode);
        Assert.Single(await AuditAsync("user.invite"));
        Assert.Equal([$"{id} {{\"method\":\"link\"}}"], await AuditAsync("auth.password.set"));
        Assert.Single(await AuditAsync("auth.password.set.failed"));
    }

    [Fact]
    public async Task NewUser_NeedsAName_AndTheUserNameOnlyOnce()
    {
        await InviteAsync("gail");
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = "gail", displayName = "Another Gail" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = " ", displayName = "Nobody" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = "noname", displayName = " " }, Ct)).StatusCode);
        var (cara, _) = await UserAsync("cara");
        Assert.Equal(HttpStatusCode.Forbidden, (await cara.PostAsJsonAsync("/api/v1/admin/users/invite", new { userName = "x", displayName = "X" }, Ct)).StatusCode);
    }

    /// <summary>Every refusal reads the same, so the answer does not tell which accounts exist or what state they are in.</summary>
    [Fact]
    public async Task SetPassword_RefusesAWrongLink_TheSameWayForEveryAccount()
    {
        var (gail, token) = await InviteAsync("gail");
        var breakGlass = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();

        foreach (var (user, t) in new[] { (gail, "not-the-token"), (Guid.NewGuid(), token), (breakGlass, token) })
        {
            var r = await SetPasswordAsync(user, t, Password);
            Assert.Equal((HttpStatusCode.BadRequest, "The link is not valid."), (r.StatusCode, await DetailAsync(r)));
        }
        // A right link with a password that is too short says so, and the link still works afterwards.
        var weak = await SetPasswordAsync(gail, token, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.True((await weak.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("password", out _));
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(gail, token, Password)).StatusCode);
    }

    [Fact]
    public async Task PasswordLink_ForAnExistingUser_ReplacesThePassword_AndEndsTheirSessions()
    {
        var (gail, id) = await UserAsync("gail");
        var (cara, _) = await UserAsync("cara");
        Assert.Equal(HttpStatusCode.Forbidden, (await cara.PostAsync($"/api/v1/admin/users/{id}/password-link", null, Ct)).StatusCode);

        var link = await _admin.PostAsync($"/api/v1/admin/users/{id}/password-link", null, Ct);
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var token = (await link.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("setPasswordToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await gail.GetAsync("/api/v1/me", Ct)).StatusCode); // asking for a link changes nothing yet

        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(id, token, "the-new-long-password-3")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await gail.GetAsync("/api/v1/me", Ct)).StatusCode); // the session opened with the old password
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(_f.NewClient(), "gail")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(_f.NewClient(), "gail", "the-new-long-password-3")).StatusCode);
        Assert.Equal([$"{id} {{}}"], await AuditAsync("user.password.link"));

        // Not for the break-glass account (its password is the server's secret), and not for an account that is not there.
        var breakGlass = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsync($"/api/v1/admin/users/{breakGlass}/password-link", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"/api/v1/admin/users/{Guid.NewGuid()}/password-link", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Delete_ClosesTheAccountAtOnce_AndKeepsItsHistory()
    {
        var (gail, id) = await UserAsync("gail");
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = id.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        var page = (await (await gail.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Gail's page", contentJson = Doc }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;
        (await gail.PutAsJsonAsync($"/api/v1/pages/{page.Id}/draft", new { title = "Draft", contentJson = Doc, baseVersion = "1.0" }, Ct)).EnsureSuccessStatusCode();
        var made = await gail.PostAsJsonAsync("/api/v1/tokens", new { name = "agent", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 1 }, Ct);
        var bearer = _f.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/spaces", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/admin/users/{id}", Ct)).StatusCode);

        // Closed at once: the open session, the token, a new sign-in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await gail.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/v1/spaces", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(_f.NewClient(), "gail")).StatusCode);
        // Gone from the list, and what the account held is gone with it.
        Assert.DoesNotContain((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Ct)).EnumerateArray(), u => u.GetProperty("id").GetGuid() == id);
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.SpaceRoleAssignments.AnyAsync(a => a.PrincipalId == id.ToString(), Ct));
            Assert.False(await db.PageDrafts.AnyAsync(d => d.UserId == id, Ct));
            Assert.True(await db.ApiTokens.Where(t => t.OwnerUserId == id).AllAsync(t => t.RevokedAt != null, Ct));
            var row = await db.Users.SingleAsync(u => u.Id == id, Ct);
            Assert.True(row is { PasswordHash: null, Email: null, IsAdmin: false } && row.DeletedAt is not null && row.DeactivatedAt is not null);
        });
        // History stays readable: the page still names its author, and so does the audit log.
        Assert.Equal("Gail", (await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{page.Id}", Ct))!.UpdatedBy);
        Assert.Contains((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?take=200", Ct)).EnumerateArray(), a => a.GetProperty("action").GetString() == "auth.signin" && a.GetProperty("actor").GetString() == "Gail");
        Assert.Equal([$"{id} {{\"userName\":\"gail\"}}"], await AuditAsync("user.delete"));

        // The user name is free again, and the new account is not the old one.
        var (again, _) = await InviteAsync("gail");
        Assert.NotEqual(id, again);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/api/v1/admin/users/{id}", Ct)).StatusCode); // already deleted
    }

    [Fact]
    public async Task Delete_Refuses_Yourself_TheBreakGlassAccount_AndTheLastAdministrator()
    {
        var breakGlass = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        var (adam, adamId) = await UserAsync("adam", admin: true);
        var (cara, caraId) = await UserAsync("cara");

        async Task RefusedAsync(HttpClient by, Guid user, string why)
        {
            var r = await by.DeleteAsync($"/api/v1/admin/users/{user}", Ct);
            Assert.Equal((HttpStatusCode.Conflict, why), (r.StatusCode, await DetailAsync(r)));
        }
        await RefusedAsync(adam, adamId, "You can't delete your own account.");
        await RefusedAsync(adam, breakGlass, "The break-glass account can't be deleted.");
        // Adam is the only Administrator besides the break-glass account. Without one the app would ask to be set up again.
        await RefusedAsync(_admin, adamId, "The last administrator can't be deleted.");
        Assert.Equal(HttpStatusCode.Forbidden, (await cara.DeleteAsync($"/api/v1/admin/users/{adamId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adam.DeleteAsync($"/api/v1/admin/users/{Guid.NewGuid()}", Ct)).StatusCode);

        var (_, evaId) = await UserAsync("eva", admin: true);
        Assert.Equal(HttpStatusCode.NoContent, (await adam.DeleteAsync($"/api/v1/admin/users/{evaId}", Ct)).StatusCode); // with two, one can go
        Assert.Equal(HttpStatusCode.NoContent, (await adam.DeleteAsync($"/api/v1/admin/users/{caraId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adam.GetAsync("/api/v1/me", Ct)).StatusCode);
    }
}
