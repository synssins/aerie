using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration;

/// <summary>
/// The application inventory: applications (name, vendor, owner, notes) and their installs (one per server, a version), with the
/// history of every change. Kept by hand, by CSV import (preview first, the same file twice changes nothing), and by a sync that
/// plugins and scripts call. Admins choose who views and who edits. Pages say which applications they apply to.
/// </summary>
public sealed class InventoryTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Body"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

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

    private async Task<Guid> AppAsync(string name, string? vendor = null, HttpClient? by = null)
    {
        var r = await (by ?? _admin).PostAsJsonAsync("/api/v1/inventory/apps", new { name, vendor }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> ReadAsync(Guid app, HttpClient? by = null) =>
        await (by ?? _admin).GetFromJsonAsync<JsonElement>($"/api/v1/inventory/apps/{app}", Ct);

    private static Task<HttpResponseMessage> SetInstallAsync(HttpClient by, Guid app, string server, string version) =>
        by.PutAsJsonAsync($"/api/v1/inventory/apps/{app}/installs", new { server, version }, Ct);

    private Task<HttpResponseMessage> ImportAsync(string csv, bool apply, bool removeMissing = false) =>
        _admin.PostAsJsonAsync("/api/v1/inventory/import", new { csv, apply, removeMissing }, Ct);

    [Fact]
    public async Task ByHand_AnApplication_ItsInstalls_AndTheirHistory()
    {
        var (_, ownerId) = await UserAsync("erin");
        var app = await AppAsync("Acme ERP", "Acme");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"/api/v1/inventory/apps/{app}", new { name = "Acme ERP", vendor = "Acme Corp", notes = "Finance", ownerType = "User", ownerId = ownerId.ToString() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/v1/inventory/apps", new { name = "acme erp" }, Ct)).StatusCode); // name in use
        Assert.Equal(HttpStatusCode.OK, (await SetInstallAsync(_admin, app, "ERP01", "4.2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetInstallAsync(_admin, app, "erp01", "4.3")).StatusCode); // the same server, any case
        Assert.Equal(HttpStatusCode.OK, (await SetInstallAsync(_admin, app, "ERP02", "4.3")).StatusCode);

        var read = await ReadAsync(app);
        Assert.Equal(("Acme Corp", "Finance", "erin"), (read.GetProperty("vendor").GetString(), read.GetProperty("notes").GetString(), read.GetProperty("owner").GetProperty("name").GetString()));
        var installs = read.GetProperty("installs").EnumerateArray().ToList();
        Assert.Equal(["ERP01", "ERP02"], installs.Select(i => i.GetProperty("server").GetString()));
        var erp01 = installs[0];
        Assert.Equal("4.3", erp01.GetProperty("version").GetString());
        Assert.Equal([("Added", null, "4.2"), ("Changed", "4.2", "4.3")], erp01.GetProperty("history").EnumerateArray()
            .Select(h => (h.GetProperty("kind").GetString(), h.GetProperty("from").GetString(), h.GetProperty("to").GetString())).Reverse());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/inventory/installs/{erp01.GetProperty("id").GetGuid()}", Ct)).StatusCode);
        Assert.Single((await ReadAsync(app)).GetProperty("installs").EnumerateArray());
        Assert.Equal(["ERP02"], (await _admin.GetFromJsonAsync<string[]>("/api/v1/inventory/servers", Ct))!);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.CountAsync(a => a.Action.StartsWith("inventory."), Ct) >= 5));
    }

    [Fact]
    public async Task CsvImport_PreviewsFirst_AndTheSameFileTwice_ChangesNothing()
    {
        const string Csv = "Application,Vendor,Server,Version\nAcme ERP,Acme,ERP01,4.2\n\"Mail, Gateway\",Mailco,MX01,10.1\nAcme ERP,Acme,ERP02,4.2\n";
        var preview = await (await ImportAsync(Csv, apply: false)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((3, 0, 2), (preview.GetProperty("added").GetInt32(), preview.GetProperty("unchanged").GetInt32(), preview.GetProperty("newApplications").GetInt32()));
        Assert.Empty(await _admin.GetFromJsonAsync<JsonElement[]>("/api/v1/inventory/apps", Ct) ?? []); // a preview stores nothing

        var applied = await (await ImportAsync(Csv, apply: true)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(3, applied.GetProperty("added").GetInt32());
        var again = await (await ImportAsync(Csv, apply: true)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((0, 0, 3, 0), (again.GetProperty("added").GetInt32(), again.GetProperty("changed").GetInt32(), again.GetProperty("unchanged").GetInt32(), again.GetProperty("removed").GetInt32()));

        // A patch: one version up; with "remove missing", ERP02 is gone from the file, so its install goes.
        var patch = await (await ImportAsync("Application,Server,Version\nAcme ERP,ERP01,4.3\n", apply: true, removeMissing: true)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((1, 0), (patch.GetProperty("changed").GetInt32(), patch.GetProperty("removed").GetInt32())); // only servers in the file are touched
        var apps = await _admin.GetFromJsonAsync<JsonElement[]>("/api/v1/inventory/apps?q=acme", Ct);
        var erp = await ReadAsync(apps!.Single().GetProperty("id").GetGuid());
        Assert.Equal(["4.3", "4.2"], erp.GetProperty("installs").EnumerateArray().Select(i => i.GetProperty("version").GetString()));
        Assert.Contains("CSV import", erp.GetProperty("installs")[0].GetProperty("changedBy").GetString());

        var bad = await ImportAsync("Application,Server\nAcme ERP,ERP01\n", apply: false);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Sync_ByAScriptWithAWriteToken_IsTheSameUpdate_AndNamesItsSource()
    {
        var (bot, botId) = await UserAsync("bot");
        (await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new { view = new[] { new { kind = "Everyone" } }, edit = new[] { new { kind = "User", id = botId.ToString() } } }, Ct)).EnsureSuccessStatusCode();
        var made = await (await bot.PostAsJsonAsync("/api/v1/tokens", new { name = "CMDB feed", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Write" } }, days = 1 }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = _f.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("token").GetString());

        object Body(string version) => new { source = "cmdb", rows = new[] { new { application = "Acme ERP", vendor = "Acme", server = "ERP01", version } } };
        var first = await token.PostAsJsonAsync("/api/v1/inventory/sync", Body("4.2"), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("added").GetInt32());
        Assert.Equal(1, (await (await token.PostAsJsonAsync("/api/v1/inventory/sync", Body("4.2"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("unchanged").GetInt32());
        var app = (await _admin.GetFromJsonAsync<JsonElement[]>("/api/v1/inventory/apps", Ct))!.Single().GetProperty("id").GetGuid();
        Assert.Contains("cmdb", (await ReadAsync(app)).GetProperty("installs")[0].GetProperty("changedBy").GetString());

        // Without edit rights the sync is refused.
        (await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new { view = new[] { new { kind = "Everyone" } }, edit = Array.Empty<object>() }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync("/api/v1/inventory/sync", Body("4.3"), Ct)).StatusCode);
    }

    private HttpClient Bearer(string text)
    {
        var c = _f.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", text);
        return c;
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("detail", out var d) ? d.GetString() : null;

    [Fact]
    public async Task AServiceToken_SyncsOnceTheAdminAllowsIt_AndRefusalsSayWhy()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "cmdb", name = "cmdb" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        async Task<(HttpClient Client, Guid Id)> TokenAsync(string access)
        {
            var made = await (await _admin.PostAsJsonAsync("/api/v1/tokens", new { name = $"CMDB {access}", kind = "Service", scopes = new[] { new { spaceId = (Guid?)space, access } }, days = 1 }, Ct))
                .Content.ReadFromJsonAsync<JsonElement>(Ct);
            return (Bearer(made.GetProperty("token").GetString()!), made.GetProperty("info").GetProperty("id").GetGuid());
        }
        var (writer, writerId) = await TokenAsync("Write");
        var (reader, _) = await TokenAsync("Read");
        var body = new { source = "cmdb", rows = new[] { new { application = "Acme ERP", vendor = "Acme", server = "ERP01", version = "4.2" } } };

        var refused = await writer.PostAsJsonAsync("/api/v1/inventory/sync", body, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("This token's owner can't edit the inventory.", await DetailAsync(refused));
        var readOnly = await reader.PostAsJsonAsync("/api/v1/inventory/sync", body, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Equal("This token needs the Write scope.", await DetailAsync(readOnly));

        var saved = await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new
        {
            view = new[] { new { kind = "Everyone", id = (string?)null } },
            edit = new[] { new { kind = "Token", id = (string?)writerId.ToString() } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("CMDB Write", (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("edit")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await writer.PostAsJsonAsync("/api/v1/inventory/sync", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new { view = Array.Empty<object>(), edit = new[] { new { kind = "Token", id = Guid.NewGuid().ToString() } } }, Ct)).StatusCode);

        // A person who may not edit hears why too.
        var (ben, _) = await UserAsync("benny");
        var app = (await _admin.GetFromJsonAsync<JsonElement[]>("/api/v1/inventory/apps", Ct))!.Single().GetProperty("id").GetGuid();
        Assert.Equal("You can't edit the inventory.", await DetailAsync(await SetInstallAsync(ben, app, "S1", "1")));
    }

    [Fact]
    public async Task ANewDatabase_HasNoAccessRule_SoOnlyAdminsViewTheInventory()
    {
        await _f.WithDbAsync(async db => Assert.False(await db.InvAccessRules.AnyAsync(Ct)));
        var (ben, _) = await UserAsync("ben");
        var me = await ben.GetFromJsonAsync<JsonElement>("/api/v1/inventory/me", Ct);
        Assert.Equal((false, false), (me.GetProperty("canView").GetBoolean(), me.GetProperty("canEdit").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync("/api/v1/inventory/apps", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync("/api/v1/inventory/servers", Ct)).StatusCode);
        var admin = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/inventory/me", Ct);
        Assert.True(admin.GetProperty("canView").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/v1/inventory/apps", Ct)).StatusCode);
    }

    [Fact]
    public async Task Access_IsWhatTheAdminSets_BySpaceRolePersonOrGroup()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "erp", name = "erp" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var (ann, annId) = await UserAsync("ann");
        var (ben, _) = await UserAsync("ben");
        var app = await AppAsync("Acme ERP");

        // By default only Admins view and edit.
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/v1/inventory/apps/{app}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetInstallAsync(ben, app, "S1", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("/api/v1/inventory/apps", Ct)).StatusCode);

        // View: members of the space; edit: holders of Contributor.
        var contributor = (await _admin.GetFromJsonAsync<JsonElement[]>("/api/v1/roles", Ct))!.Single(r => r.GetProperty("name").GetString() == "Contributor").GetProperty("id").GetString();
        var r = await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new
        {
            view = new[] { new { kind = "Space", id = space.ToString() } },
            edit = new[] { new { kind = "Role", id = contributor } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/v1/inventory/apps/{app}", Ct)).StatusCode);
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = annId.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync($"/api/v1/inventory/apps/{app}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetInstallAsync(ann, app, "S1", "1")).StatusCode);
        var me = await ann.GetFromJsonAsync<JsonElement>("/api/v1/inventory/me", Ct);
        Assert.Equal((true, true), (me.GetProperty("canView").GetBoolean(), me.GetProperty("canEdit").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PutAsJsonAsync("/api/v1/admin/inventory/access", new { view = Array.Empty<object>(), edit = Array.Empty<object>() }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new { view = new[] { new { kind = "Planet", id = "x" } }, edit = Array.Empty<object>() }, Ct)).StatusCode);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.InventoryAccessSet, Ct)));
    }

    [Fact]
    public async Task APage_SaysWhatItAppliesTo_ItsOwnerAndReviewCycle_AndTheApplicationListsIt()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "ops", name = "ops" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "ERP error 42", contentJson = Doc }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var app = await AppAsync("Acme ERP");
        await SetInstallAsync(_admin, app, "ERP01", "4.2");
        var group = await (await _admin.PostAsJsonAsync("/api/v1/admin/groups", new { name = "ERP Support Team" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        var r = await _admin.PutAsJsonAsync($"/api/v1/pages/{page}/details", new
        {
            appliesTo = new[] { new { applicationId = app, servers = new[] { "erp01" } } },
            ownerType = "Group", ownerId = group.GetProperty("key").GetString(), reviewMonths = 6,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var details = (await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct)).GetProperty("details");
        Assert.Equal("Acme ERP", details.GetProperty("appliesTo")[0].GetProperty("name").GetString());
        Assert.Equal(["ERP01"], details.GetProperty("appliesTo")[0].GetProperty("servers").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(("ERP Support Team", 6), (details.GetProperty("owner").GetProperty("name").GetString(), details.GetProperty("reviewMonths").GetInt32()));

        var pages = (await ReadAsync(app)).GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal("ERP error 42", Assert.Single(pages).GetProperty("title").GetString());
        // An application a page applies to is not deleted; the count is given.
        var refused = await _admin.DeleteAsync($"/api/v1/inventory/apps/{app}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Used on 1 page.", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());

        // Someone who views the inventory but cannot see the page does not see it listed under the application.
        (await _admin.PutAsJsonAsync("/api/v1/admin/inventory/access", new { view = new[] { new { kind = "Everyone" } }, edit = Array.Empty<object>() }, Ct)).EnsureSuccessStatusCode();
        var (_, _) = await UserAsync("vic");
        var vic = _f.NewClient();
        (await vic.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "vic", password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Empty((await ReadAsync(app, vic)).GetProperty("pages").EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync($"/api/v1/pages/{page}/details", new { appliesTo = new[] { new { applicationId = Guid.NewGuid(), servers = Array.Empty<string>() } } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync($"/api/v1/pages/{page}/details", new { appliesTo = Array.Empty<object>(), reviewMonths = 0 }, Ct)).StatusCode);
        // Once the page is in the recycle bin, the application can go.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/pages/{page}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/v1/inventory/apps/{app}", Ct)).StatusCode);
    }

    [Fact]
    public async Task PageDetails_AreAnEditorsToChange()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "desk", name = "desk" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "P", contentJson = Doc }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var (rita, ritaId) = await UserAsync("rita");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = ritaId.ToString(), role = "Reader" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await rita.PutAsJsonAsync($"/api/v1/pages/{page}/details", new { appliesTo = Array.Empty<object>(), reviewMonths = 12 }, Ct)).StatusCode);
        var (_, _) = await UserAsync("out");
        var outsider = _f.NewClient();
        (await outsider.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "out", password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync($"/api/v1/pages/{page}/details", new { appliesTo = Array.Empty<object>() }, Ct)).StatusCode);
    }
}
