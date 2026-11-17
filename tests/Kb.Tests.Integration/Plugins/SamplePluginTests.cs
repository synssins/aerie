using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Web;
using Kb.Web.Import;
using Kb.Web.Pipeline;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>
/// The sample plugin (plugins/Kb.Plugin.Sample): readers acknowledge a page. It is installed here the way an Admin installs it,
/// so these tests are also the proof that a plugin built only on the contract assemblies works end to end.
/// </summary>
public sealed class SamplePluginTests : IAsyncLifetime
{
    private const string Doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]}""";
    private const string Admin = "/api/v1/admin/plugins";
    private const string Sample = "/api/v1/plugins/kb.sample";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space, _page;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Sample));
        await RestartAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        _page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Reset password" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync($"{Admin}/kb.sample/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    /// <summary>A signed-in person with a role in the space, or none.</summary>
    private async Task<HttpClient> UserAsync(string name, string? role)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name[..1].ToUpperInvariant() + name[1..], CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, "user-password-0123456789")).Succeeded);
            id = user.Id;
        }
        if (role is not null)
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private Task<HttpResponseMessage> AckAsync(HttpClient c) => c.PostAsync($"{Sample}/pages/{_page}/ack", null, Ct);
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    private async Task<int> CountAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>($"{Sample}/pages/{_page}/ack", Ct)).GetProperty("count").GetInt32();
    private Task<HttpResponseMessage> SaveAsync(bool major, string? note = null) =>
        _admin.PutAsJsonAsync($"/api/v1/pages/{_page}", new { title = "Reset password", contentJson = Doc, baseVersion = "1.0", major, note }, Ct);

    private async Task<List<string>> TablesAsync()
    {
        var tables = new List<string>();
        await _f.WithDbAsync(async db => tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'plg%'").ToListAsync(Ct));
        return tables;
    }

    [Fact]
    public async Task Sample_Installs_AndItsTableExists()
    {
        Assert.Equal(["plg_kb_sample__acks"], await TablesAsync());
        var listed = (await _admin.GetFromJsonAsync<JsonElement>(Admin, Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample");
        Assert.Equal(("Enabled", true), (listed.GetProperty("state").GetString(), listed.GetProperty("loaded").GetBoolean()));
    }

    [Fact]
    public async Task Sample_HasAScriptForTheBrowser()
    {
        var listed = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ui/plugins", Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample");
        var script = await _admin.GetAsync(listed.GetProperty("script").GetString(), Ct);

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        var text = await script.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"/shared/v1/react.js\"", text); // the app's React, not one of its own
        Assert.Contains("page.panel", text);
        // Its stylesheet is served from the same folder, where the script looks for it.
        var css = await _admin.GetAsync(listed.GetProperty("script").GetString()!.Split('?')[0].Replace("index.js", "notice.css", StringComparison.Ordinal), Ct);
        Assert.Equal((HttpStatusCode.OK, "text/css"), (css.StatusCode, css.Content.Headers.ContentType?.MediaType));
    }

    [Fact]
    public async Task Notice_IsReadByHistorySearchAndExport()
    {
        const string doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]},{"type":"sampleNotice","content":[{"type":"text","text":"Acknowledge by "},{"type":"text","text":"Friday","marks":[{"type":"bold"}]}]}]}""";
        (await _admin.PutAsJsonAsync($"/api/v1/pages/{_page}", new { title = "Reset password", contentJson = doc, baseVersion = "1.0" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal("Hello\n\n> **Notice:** Acknowledge by Friday", (await _admin.GetFromJsonAsync<PageMarkdownDto>($"/api/v1/pages/{_page}/markdown", Ct))!.Markdown);
        Assert.Equal(1, (await _admin.GetFromJsonAsync<SearchResponse>("/api/v1/search?q=friday", Ct))!.Total); // by the text inside the block
        var docx = await (await _admin.GetAsync($"/api/v1/pages/{_page}/export?format=docx", Ct)).Content.ReadAsByteArrayAsync(Ct);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
        using var body = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await body.ReadToEndAsync(Ct);
        Assert.Contains("Notice: ", xml);
        Assert.Contains("Acknowledge by Friday", xml);
    }

    [Fact]
    public async Task Ack_NeedsReadOfThePage()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await AckAsync(await UserAsync("dan", null))).StatusCode); // no role in the space: the page does not exist for them
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"{Sample}/pages/{Guid.NewGuid()}/ack", null, Ct)).StatusCode);
        var r = await AckAsync(await UserAsync("rita", "Reader"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("sample.ack", r.Headers.GetValues("X-Kb-Action").Single());
    }

    [Fact]
    public async Task Ack_TwiceBySameUser_CountsOnce()
    {
        var rita = await UserAsync("rita", "Reader");
        var first = await JsonAsync(await AckAsync(rita));
        Assert.Equal((1, true), (first.GetProperty("count").GetInt32(), first.GetProperty("mine").GetBoolean()));
        Assert.Equal(1, (await JsonAsync(await AckAsync(rita))).GetProperty("count").GetInt32());

        await AckAsync(await UserAsync("sam", "Reader"));
        Assert.Equal(2, await CountAsync(rita));
    }

    [Fact]
    public async Task AcksRead_NeedsSupervisor_AnyoneReadsTheCount()
    {
        var rita = await UserAsync("rita", "Reader");
        await AckAsync(rita);

        Assert.Equal(HttpStatusCode.Forbidden, (await rita.GetAsync($"{Sample}/pages/{_page}/acks", Ct)).StatusCode);
        Assert.Equal(1, await CountAsync(rita));
        var names = await (await UserAsync("sue", "Supervisor")).GetFromJsonAsync<JsonElement>($"{Sample}/pages/{_page}/acks", Ct);
        var entry = Assert.Single(names.GetProperty("people").EnumerateArray());
        Assert.Equal(("Rita", "1.0"), (entry.GetProperty("name").GetString(), entry.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task AcksRead_FollowsThePluginsPermission_WhenAnAdminChangesItsRole()
    {
        var rita = await UserAsync("rita", "Reader");
        await AckAsync(rita);
        Assert.Equal(HttpStatusCode.Forbidden, (await rita.GetAsync($"{Sample}/pages/{_page}/acks", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"{Admin}/kb.sample/permissions", new { roles = new Dictionary<string, string> { ["see-acknowledgements"] = "Reader" } }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await rita.GetAsync($"{Sample}/pages/{_page}/acks", Ct)).StatusCode);
    }

    [Fact]
    public async Task MajorVersion_WithoutNote_Refused_WhenTheSettingIsOn()
    {
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(major: false)).StatusCode); // the setting is off: nothing is asked
        await SetAsync("RequireNoteOnMajor", "true");

        var refused = await _admin.PutAsJsonAsync($"/api/v1/pages/{_page}", new { title = "Reset password", contentJson = Doc, baseVersion = "1.1", major = true }, Ct);
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("A major version needs a note.", (await JsonAsync(refused)).GetProperty("detail").GetString());
        var saved = await _admin.PutAsJsonAsync($"/api/v1/pages/{_page}", new { title = "Reset password", contentJson = Doc, baseVersion = "1.1", major = true, note = "New process" }, Ct);
        Assert.Equal("2.0", (await JsonAsync(saved)).GetProperty("version").GetString());
    }

    [Fact]
    public async Task MajorVersion_ClearsAcknowledgements_MinorDoesNot()
    {
        await SetAsync("ResetOnMajor", "true");
        var rita = await UserAsync("rita", "Reader");
        await AckAsync(rita);

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(major: false)).StatusCode);
        await _f.Services.GetRequiredService<ActionQueueWorker>().StopAsync(Ct); // what was queued has run
        Assert.Equal(1, await CountAsync(rita));

        await RestartAsync();
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"/api/v1/pages/{_page}", new { title = "Reset password", contentJson = Doc, baseVersion = "1.1", major = true }, Ct)).StatusCode);
        await _f.Services.GetRequiredService<ActionQueueWorker>().StopAsync(Ct);
        Assert.Equal(0, await CountAsync(_admin));
    }

    [Fact]
    public async Task Purge_RemovesOldAcknowledgements()
    {
        await AckAsync(_admin);
        await SetAsync("KeepDays", "0"); // everything is older than "0 days"

        Assert.Equal(1, await _f.Services.GetRequiredService<JobRunner>().RunOnceAsync(Ct));

        Assert.Equal(0, await CountAsync(_admin));
    }

    [Fact]
    public async Task Health_ListsItsCheckAndItsJob()
    {
        var before = (await _admin.GetFromJsonAsync<JsonElement>($"{Admin}/health", Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample");
        var check = Assert.Single(before.GetProperty("checks").EnumerateArray());
        Assert.Equal(("table", "Healthy"), (check.GetProperty("name").GetString(), check.GetProperty("status").GetString()));
        var job = Assert.Single(before.GetProperty("jobs").EnumerateArray());
        Assert.Equal(("purge", 1440, JsonValueKind.Null), (job.GetProperty("name").GetString(), job.GetProperty("everyMinutes").GetInt32(), job.GetProperty("lastRunAt").ValueKind));

        await _f.Services.GetRequiredService<JobRunner>().RunOnceAsync(Ct);

        var after = (await _admin.GetFromJsonAsync<JsonElement>($"{Admin}/health", Ct)).GetProperty("plugins").EnumerateArray().Single().GetProperty("jobs").EnumerateArray().Single();
        Assert.Equal((JsonValueKind.String, JsonValueKind.String, JsonValueKind.Null), (after.GetProperty("lastRunAt").ValueKind, after.GetProperty("nextRunAt").ValueKind, after.GetProperty("lastError").ValueKind));
        Assert.Equal(HttpStatusCode.Forbidden, (await (await UserAsync("rita", "Owner")).GetAsync($"{Admin}/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Sample_Removed_WithDeleteData_LeavesNothing()
    {
        await AckAsync(_admin);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{Admin}/kb.sample?deleteData=true", Ct)).StatusCode);
        await RestartAsync();

        Assert.Empty(await TablesAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{Sample}/pages/{_page}/ack", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/pages/{_page}", Ct)).StatusCode); // the core is as it was
    }
    /// <summary>Uploads one file to the Import inbox as the Admin, runs the worker, and answers the item's detail.</summary>
    private async Task<JsonElement> ImportAsync(string name, string text)
    {
        var id = (await JsonAsync(await _admin.PostAsJsonAsync("/api/v1/imports", new { }, Ct))).GetProperty("id").GetGuid();
        var body = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        body.Headers.Add("X-File-Name", name);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/imports/{id}/files", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/v1/imports/{id}/start", null, Ct)).StatusCode);
        var worker = _f.Services.GetRequiredService<ImportWorker>();
        for (var i = 0; i < 20 && await worker.RunOnceAsync(Ct); i++) { }
        var item = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/imports", Ct)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("batchId").GetGuid() == id);
        return await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/imports/items/{item.GetProperty("id").GetGuid()}", Ct);
    }

    [Fact]
    public async Task ANoticeFile_IsDraftedByTheSample_WithoutTheAI_AndNotOnceTheSampleIsDisabled()
    {
        // The AI is on (imports need it), but no model answers here: a call to it would fail the item.
        (await _admin.PutAsJsonAsync("/api/v1/admin/ai", new
        {
            settings = new
            {
                enabled = true, baseUrl = "http://model.invalid", model = "stub", timeoutSeconds = 5, maxTokens = 4096, temperature = 0, reasoning = false, ruleset = Kb.Core.Ai.AiGuard.DefaultRuleset,
                maxInputChars = 200000, userPerMinute = 100, tokenPerMinute = 100, features = new { import = "Everyone", format = "Off", search = "Off" },
            },
        }, Ct)).EnsureSuccessStatusCode();
        var listed = (await _admin.GetFromJsonAsync<JsonElement>(Admin, Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample");
        Assert.Equal([".notice"], listed.GetProperty("importFileKinds").EnumerateArray().Select(k => k.GetString()));

        var detail = await ImportAsync("Fire_drill.notice", "Leave by the stairs.");
        Assert.Equal("Ready", detail.GetProperty("status").GetString());
        var page = detail.GetProperty("proposal").GetProperty("pages")[0];
        Assert.Equal("Fire drill", page.GetProperty("title").GetString());
        var node = Assert.Single(JsonDocument.Parse(page.GetProperty("contentJson").GetString()!).RootElement.GetProperty("content").EnumerateArray());
        Assert.Equal(("sampleNotice", "Leave by the stairs."), (node.GetProperty("type").GetString(), node.GetProperty("content")[0].GetProperty("text").GetString()));

        Assert.Equal("Claimed", Assert.Single(detail.GetProperty("files").EnumerateArray()).GetProperty("kind").GetString());

        // Disabled, it is not loaded after the restart: its claim no longer counts. The file is read as before 0.6.0: as text, which
        // goes to the AI (here no model answers, so the item fails).
        (await _admin.PutAsJsonAsync($"{Admin}/kb.sample/state", new { enabled = false }, Ct)).EnsureSuccessStatusCode();
        await RestartAsync();
        detail = await ImportAsync("Fire_drill.notice", "Leave by the stairs.");
        Assert.Equal("Text", Assert.Single(detail.GetProperty("files").EnumerateArray()).GetProperty("kind").GetString());
        Assert.Equal("Failed", detail.GetProperty("status").GetString());
        Assert.NotEqual(Extractors.NotRead, detail.GetProperty("error").GetString());
    }
    [Fact]
    public async Task ASetting_ItsBounds_AreCheckedInPlainWords_AndItsHelpDefaultAndUnitAreListed()
    {
        var listed = (await _admin.GetFromJsonAsync<JsonElement>(Admin, Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.sample");
        var keep = listed.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "KeepDays");
        Assert.Equal(("days", "365", 0m, 3650m), (keep.GetProperty("unit").GetString(), keep.GetProperty("default").GetString(), keep.GetProperty("min").GetDecimal(), keep.GetProperty("max").GetDecimal()));
        Assert.False(string.IsNullOrEmpty(keep.GetProperty("help").GetString()));

        var r = await _admin.PutAsJsonAsync($"{Admin}/kb.sample/settings", new { values = new Dictionary<string, string> { ["KeepDays"] = "5000" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("At most 3650 days.", (await JsonAsync(r)).GetProperty("errors").GetProperty("KeepDays")[0].GetString());
        await SetAsync("KeepDays", "30");
    }

    [Fact]
    public async Task ThePluginsPanel_ReadsItsOwnRoute_ForAnAdmin_AndNeitherThePageNorTheRouteIsOpenToOthers()
    {
        Assert.Equal(HttpStatusCode.OK, (await AckAsync(_admin)).StatusCode);
        var totals = await _admin.GetFromJsonAsync<JsonElement>($"{Sample}/totals", Ct);
        Assert.Equal((1, 1), (totals.GetProperty("acknowledgements").GetInt32(), totals.GetProperty("pages").GetInt32()));

        var reader = await UserAsync("rita", "Reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Admin}/kb.sample/settings", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Sample}/totals", Ct)).StatusCode);
    }
}
