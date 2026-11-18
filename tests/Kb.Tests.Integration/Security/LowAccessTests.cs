using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kb.Tests.Integration.DataCenter;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// The Low findings of the code review of 2026-11-17 about who learns what: L1 (names on a page read), L2 (the last editor on the home
/// feed), L14 (a role for an account that is not there, and a second row), L15 (an Admin and a sensitive space's settings), L16 (the AI
/// and a sensitive space), L17 (a trashed attachment and an old attachment version), L18 (a name through a user link).
/// </summary>
public sealed class LowAccessTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x"}]}]}""";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _cora = null!, _nils = null!;
    private Guid _space, _coraId;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        _coraId = await UserAsync(_f, "cora");
        await UserAsync(_f, "nils"); // signed in, no role anywhere
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = _coraId.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        (_cora, _nils) = (await SessionAsync("cora"), await SessionAsync("nils"));
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<HttpClient> SessionAsync(string name)
    {
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<Guid> PageAsync(HttpClient by, Guid space, string title = "Runbook") =>
        (await (await by.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title, contentJson = Doc }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private Task<HttpResponseMessage> OpenAsync(string visibility) => _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { visibility }, Ct);

    // ---- L1 ----

    [Fact]
    public async Task PageRead_GivesNoOwnerToNobody_AndApplicationsOnlyToWhoViewsTheInventory()
    {
        var page = await PageAsync(_cora, _space);
        var app = (await (await _admin.PostAsJsonAsync("/api/v1/inventory/apps", new { name = "Acme ERP" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await _admin.PutAsJsonAsync($"/api/v1/inventory/apps/{app}/installs", new { server = "ERP01", version = "4.2" }, Ct);
        (await _admin.PutAsJsonAsync($"/api/v1/pages/{page}/details", new { appliesTo = new[] { new { applicationId = app, servers = new[] { "ERP01" } } }, ownerType = "User", ownerId = _coraId.ToString() }, Ct)).EnsureSuccessStatusCode();
        (await OpenAsync("Anyone")).EnsureSuccessStatusCode();

        var nobody = (await _f.NewClient().GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct)).GetProperty("details");
        Assert.Equal(0, nobody.GetProperty("appliesTo").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, nobody.GetProperty("owner").ValueKind);

        // Signed in, but the inventory is not theirs to view (a new install lets only Admins): no application or server names.
        var reader = (await _nils.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct)).GetProperty("details");
        Assert.Equal(0, reader.GetProperty("appliesTo").GetArrayLength());
        Assert.Equal("cora Person", reader.GetProperty("owner").GetProperty("name").GetString());

        var admin = (await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{page}", Ct)).GetProperty("details");
        Assert.Equal("Acme ERP", admin.GetProperty("appliesTo")[0].GetProperty("name").GetString());
    }

    // ---- L2 ----

    [Fact]
    public async Task HomeFeed_NamesTheLastEditorOnlyToMembers()
    {
        await PageAsync(_cora, _space);
        (await OpenAsync("SignedIn")).EnsureSuccessStatusCode();
        string By(JsonElement home) => home.GetProperty("recent")[0].GetProperty("updatedBy").GetString()!;
        Assert.Equal("", By(await _nils.GetFromJsonAsync<JsonElement>("/api/v1/home", Ct)));
        Assert.Equal("cora Person", By(await _cora.GetFromJsonAsync<JsonElement>("/api/v1/home", Ct)));
    }

    // ---- L14 ----

    [Fact]
    public async Task Role_IsForAnAccountThatExists_AndOneRowPerPerson()
    {
        Task<HttpResponseMessage> GrantAsync(string id, string role) => _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id, role }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await GrantAsync(Guid.NewGuid().ToString(), "Reader")).StatusCode);
        // The same person, the id typed in upper case: the row is changed, not doubled.
        (await GrantAsync(_coraId.ToString().ToUpperInvariant(), "Reader")).EnsureSuccessStatusCode();
        await _f.WithDbAsync(async db =>
        {
            var rows = await db.SpaceRoleAssignments.Where(a => a.SpaceId == _space).ToListAsync(Ct);
            Assert.Equal([_coraId.ToString("D")], rows.Select(r => r.PrincipalId));
        });
    }

    // ---- L15 ----

    [Fact]
    public async Task Admin_WithoutARole_DoesNotReadWhatASensitiveSpaceSaysAboutItself()
    {
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { description = "Where payroll is run", isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        var listed = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/spaces", Ct)).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == _space);
        Assert.Equal("Operations", listed.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("description").ValueKind);
        // The answer to a change does not hand it back either. (That an Admin sets a sensitive space's name and settings without a role
        // is intended: SettingsTests.Admin_ListsAndConfiguresSensitiveSpace_AndClearsItsFlagOnlyWithAccess.)
        var changed = await (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { agingDays = 30 }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("description").ValueKind);
        // With emergency access, which tells the Owners, the Admin has a role and reads it.
        Assert.True((await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/emergency-access", new { reason = "Asked by the Owner" }, Ct)).IsSuccessStatusCode);
        listed = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/spaces", Ct)).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == _space);
        Assert.Equal("Where payroll is run", listed.GetProperty("description").GetString());
    }

    // ---- L16 ----

    [Fact]
    public async Task AiAssistAndFormatting_AreRefusedInASensitiveSpace()
    {
        var page = await PageAsync(_cora, _space);
        // The AI features are open to everyone here, so the refusal below is about the space and nothing else.
        (await _admin.PutAsJsonAsync("/api/v1/admin/ai", new
        {
            settings = new
            {
                enabled = true, baseUrl = "http://model.local", model = "stub", timeoutSeconds = 30, maxTokens = 4096, temperature = 0, reasoning = false, ruleset = Kb.Core.Ai.AiGuard.DefaultRuleset,
                maxInputChars = 200000, userPerMinute = 100, tokenPerMinute = 100, features = new { import = "Everyone", format = "Everyone", assist = "Everyone", search = "Off" },
            },
        }, Ct)).EnsureSuccessStatusCode();
        (await _admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_space}", new { isSensitive = true }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _cora.PostAsJsonAsync($"/api/v1/spaces/{_space}/assist", new { title = "T", text = "some notes" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _cora.PostAsJsonAsync($"/api/v1/pages/{page}/format", new { contentJson = Doc }, Ct)).StatusCode);
    }

    // ---- L17 ----

    [Fact]
    public async Task TrashedAttachment_AndOldVersion_NeedTheBinAndHistoryRights()
    {
        var (cora, rita) = (Basic(_f, "cora"), Basic(_f, "rita"));
        var ritaId = await UserAsync(_f, "rita");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = ritaId.ToString(), role = "Reader" }, Ct)).EnsureSuccessStatusCode();
        var page = (await (await cora.PostJsonAsync("/rest/api/content", new { type = "page", title = "Files", space = new { key = "OPS" }, body = new { storage = new { value = "<p>x</p>", representation = "storage" } } }))
            .JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;
        async Task<JsonElement> UploadAsync(string path, string text)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", "notes.txt" } };
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
            req.Headers.Add(Kb.Web.DataCenter.DcRest.XsrfHeader, "no-check");
            return await (await cora.SendAsync(req, Ct)).JsonAsync(HttpStatusCode.OK);
        }
        var made = (await UploadAsync($"/rest/api/content/{page}/child/attachment", "first notes")).GetProperty("results")[0];
        var (id, download) = (made.GetProperty("id").GetString()!, made.GetProperty("_links").GetProperty("download").GetString()!.Split('?')[0]);
        await UploadAsync($"/rest/api/content/{page}/child/attachment/{id}/data", "second notes");

        // An old version is history: a Reader of the space has it, someone who reads only because the space is open does not.
        (await OpenAsync("SignedIn")).EnsureSuccessStatusCode();
        var nils = Basic(_f, "nils");
        Assert.Equal("second notes", await nils.GetStringAsync(download, Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await nils.GetAsync(download + "?version=1", Ct)).StatusCode);
        Assert.Equal("first notes", await rita.GetStringAsync(download + "?version=1", Ct));

        // A trashed attachment is in the bin: a Reader does not manage it.
        (await cora.DeleteAsync($"/rest/api/content/{page}/child/attachment/{id}", Ct)).EnsureSuccessStatusCode();
        await (await rita.GetAsync($"/rest/api/content/{id}?status=trashed", Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    // ---- L18 ----

    [Fact]
    public async Task UserLink_GivesANameOnlyToWhoMayLookPeopleUp()
    {
        var hidden = await UserAsync(_f, "zed", u => u.DisplayName = "Zed Hidden");
        string Body(string key) => $"<p><ac:link><ri:user ri:userkey=\"{key}\" /></ac:link></p>";
        async Task<string> ViewAsync(HttpClient by, Guid user) =>
            (await (await by.PostJsonAsync("/rest/api/contentbody/convert/view", new { representation = "storage", value = Body(user.ToString("N")) })).JsonAsync(HttpStatusCode.OK)).GetProperty("value").GetString()!;
        // nils holds no role anywhere, so may not look people up.
        var nils = Basic(_f, "nils");
        Assert.DoesNotContain("Zed Hidden", await ViewAsync(nils, hidden), StringComparison.Ordinal);
        Assert.Contains("nils Person", await ViewAsync(nils, (await UserIdAsync(_f, "nils"))!.Value), StringComparison.Ordinal); // oneself, always
        Assert.Contains("Zed Hidden", await ViewAsync(Basic(_f, "cora"), hidden), StringComparison.Ordinal);
    }
}
