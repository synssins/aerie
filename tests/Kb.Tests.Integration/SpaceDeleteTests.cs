using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Plugins;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// Deleting a space. Only an Admin does it, and only with the phrase typed out. The space and everything in it go to the recycle
/// bin: nobody sees any of it any more, by list or by id, and an Admin restores it whole from the admin bin.
/// </summary>
public sealed class SpaceDeleteTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"findmeword"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _olga = null!;
    private Guid _space, _olgaId;
    private PageDto _page = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        (_olga, _olgaId) = await UserAsync("olga", "Owner");
        _page = (await (await _olga.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Runbook", contentJson = Doc }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name, string? role, bool admin = false)
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
        (await Codes.SignInAsync(_f, c, name, Password)).EnsureSuccessStatusCode();
        return (c, id);
    }

    private Task<HttpResponseMessage> DeleteAsync(HttpClient by, string? confirm) =>
        by.PostAsJsonAsync($"/api/v1/admin/spaces/{_space}/delete", new { confirm }, Ct);

    private async Task<List<string>> AuditAsync(string action)
    {
        List<string> rows = [];
        await _f.WithDbAsync(async db => rows = [.. (await db.AuditEntries.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Sequence).ToListAsync(Ct)).Select(a => $"{a.TargetId} {a.DataJson}")]);
        return rows;
    }

    [Fact]
    public async Task Delete_NeedsAnAdmin_AndThePhraseTypedOut()
    {
        // The Owner of the space is not an Admin: refused before the phrase is looked at, right or wrong.
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(_olga, "delete desk")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeleteAsync(_f.NewClient(), "delete desk")).StatusCode);

        // The server asks for the phrase itself. A dialog in a browser is not the control.
        foreach (var wrong in new[] { null, "", "delete", "desk", "delete service desk", "delete  desk x" })
        {
            var r = await DeleteAsync(_admin, wrong);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("Type: delete desk", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("confirm")[0].GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await _olga.GetAsync($"/api/v1/spaces/{_space}", Ct)).StatusCode);
        Assert.Empty(await AuditAsync("space.delete"));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(_admin, "delete desk")).StatusCode);

        Assert.Equal([$"{_space} {{\"key\":\"desk\",\"name\":\"Service Desk\",\"pages\":\"1\"}}"], await AuditAsync("space.delete"));
        // Its Owners hear of it.
        var alerts = (await _olga.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Ct)).EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.Contains("Service Desk was deleted", alerts);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(_admin, "delete desk")).StatusCode); // it is not there to delete again
    }

    [Fact]
    public async Task DeletedSpace_IsGoneForEveryone_ByIdAsWell()
    {
        var body = new ByteArrayContent("hello"u8.ToArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", "note.txt");
        var attachment = (await (await _olga.PostAsync($"/api/v1/pages/{_page.Id}/attachments", body, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var made = await _olga.PostAsJsonAsync("/api/v1/tokens", new { name = "agent", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 1 }, Ct);
        var bearer = _f.NewClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString());
        var (adam, _) = await UserAsync("adam", null, admin: true);
        Assert.Equal(1, (await _olga.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=findmeword", Ct))!.Total);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(_admin, "delete desk")).StatusCode);

        // Its Owner, an Admin and a token: none of them has the space, its pages or its files, in a list or by id.
        foreach (var who in new[] { _olga, adam, _admin })
        {
            Assert.DoesNotContain((await who.GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", Ct))!, s => s.Id == _space);
            foreach (var path in new[] { $"/api/v1/spaces/{_space}", $"/api/v1/spaces/{_space}/tree", $"/api/v1/spaces/{_space}/roles", $"/api/v1/spaces/{_space}/bin", $"/api/v1/pages/{_page.Id}",
                         $"/api/v1/pages/{_page.Id}/markdown", $"/api/v1/pages/{_page.Id}/versions", $"/api/v1/pages/{_page.Id}/export?format=docx", $"/api/v1/attachments/{attachment}" })
                Assert.Equal((path, HttpStatusCode.NotFound), (path, (await who.GetAsync(path, Ct)).StatusCode));
            Assert.Equal(0, (await who.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=findmeword", Ct))!.Total);
            Assert.Equal(HttpStatusCode.NotFound, (await who.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "New" }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await who.PutAsJsonAsync($"/api/v1/pages/{_page.Id}", new { title = "x", contentJson = Doc, baseVersion = "1.0" }, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await bearer.GetAsync($"/api/v1/pages/{_page.Id}", Ct)).StatusCode);
        Assert.DoesNotContain((await _admin.GetFromJsonAsync<List<SpaceDto>>("/api/v1/admin/spaces", Ct))!, s => s.Id == _space);
        Assert.DoesNotContain((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/bin", Ct)).EnumerateArray(), i => i.GetProperty("spaceId").GetGuid() == _space);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { name = "Back door" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adam.PostAsJsonAsync($"/api/v1/spaces/{_space}/emergency-access", new { reason = "Look inside." }, Ct)).StatusCode);
        // Nothing was destroyed: the rows are all there, and so are the roles, which give nothing while the space is deleted.
        await _f.WithDbAsync(async db =>
        {
            Assert.True(await db.Pages.IgnoreQueryFilters().AnyAsync(p => p.Id == _page.Id && p.DeletedAt == null, Ct));
            Assert.True(await db.SpaceRoleAssignments.AnyAsync(a => a.SpaceId == _space && a.PrincipalId == _olgaId.ToString(), Ct));
        });
    }

    [Fact]
    public async Task Restore_BringsTheSpaceBackWhole_AndOnlyAnAdminRestores()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(_admin, "delete desk")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await _olga.GetAsync("/api/v1/admin/spaces/deleted", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _olga.PostAsync($"/api/v1/admin/spaces/{_space}/restore", null, Ct)).StatusCode);
        var listed = Assert.Single((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/spaces/deleted", Ct)).EnumerateArray());
        Assert.Equal((_space, "desk", "Service Desk", 1), (listed.GetProperty("id").GetGuid(), listed.GetProperty("key").GetString(), listed.GetProperty("name").GetString(), listed.GetProperty("pages").GetInt32()));
        Assert.False(string.IsNullOrEmpty(listed.GetProperty("deletedBy").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/admin/spaces/{_space}/restore", null, Ct)).StatusCode);

        Assert.Equal("Owner", (await _olga.GetFromJsonAsync<SpaceDto>($"/api/v1/spaces/{_space}", Ct))!.MyRole); // her role is hers again
        Assert.Equal(Doc, (await _olga.GetFromJsonAsync<PageDto>($"/api/v1/pages/{_page.Id}", Ct))!.ContentJson);
        Assert.Equal(1, (await _olga.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=findmeword", Ct))!.Total);
        Assert.Empty((await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/spaces/deleted", Ct)).EnumerateArray());
        Assert.Equal([$"{_space} {{\"key\":\"desk\",\"name\":\"Service Desk\"}}"], await AuditAsync("space.restore"));
        // Nothing to restore: a space that is not deleted, one that never was.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"/api/v1/admin/spaces/{_space}/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"/api/v1/admin/spaces/{Guid.NewGuid()}/restore", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Key_OfADeletedSpace_StaysTaken()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(_admin, "delete desk")).StatusCode);

        // The space can come back, so its key is not given to another. A key made from a name steps past it.
        var same = await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "Another desk" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        var derived = await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Desk" }, Ct);
        Assert.Equal((HttpStatusCode.Created, "desk-2"), (derived.StatusCode, (await derived.Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Key));
    }

    /// <summary>A role is given to a user by id. A row that names no user is refused, and one that is there already does not stop the Owners being told.</summary>
    [Fact]
    public async Task ARoleRow_ThatNamesNoUser_IsRefused_AndDoesNotBreakTheDelete()
    {
        var r = await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = "the-service-desk", role = "Reader" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await _f.WithDbAsync(async db =>
        {
            db.SpaceRoleAssignments.Add(new SpaceRoleAssignment { SpaceId = _space, PrincipalType = PrincipalType.User, PrincipalId = "the-service-desk", RoleId = Kb.Core.Spaces.SpacePerms.IdOf(SpaceRole.Owner) });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(_admin, "delete desk")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/admin/spaces/{_space}/restore", null, Ct)).StatusCode);
        var alerts = (await _olga.GetFromJsonAsync<JsonElement>("/api/v1/notifications", Ct)).EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.Contains("Service Desk was deleted", alerts);
    }

    /// <summary>An Admin has nothing in a sensitive space. Deleting it is still the Admin's to do; reading it is not, before or after.</summary>
    [Fact]
    public async Task SensitiveSpace_CanBeDeletedAndRestored_WithoutGivingTheAdminItsContent()
    {
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        var (adam, _) = await UserAsync("adam", null, admin: true);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(adam, "delete desk")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adam.PostAsync($"/api/v1/admin/spaces/{_space}/restore", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await adam.GetAsync($"/api/v1/pages/{_page.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _olga.GetAsync($"/api/v1/pages/{_page.Id}", Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True((await db.Spaces.SingleAsync(s => s.Id == _space, Ct)).IsSensitive));
    }
}
