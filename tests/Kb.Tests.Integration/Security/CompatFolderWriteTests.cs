using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Kb.Tests.Integration.DataCenter;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// M7 of the code review of 2026-11-17: every write of the compatible API to a folder or to an attachment passes a plugin's hooks and an
/// enforcing plugin's lock. A folder's body goes through page.save, a file attached to a folder through attachment.add, and a new version,
/// a rename or a move of an attachment past attachment.add's lock and hooks. The test probe hooks every core action and refuses the one its
/// Refuse setting names; it is enforcing, so when the core disables it the actions it hooked are locked.
/// </summary>
public sealed class CompatFolderWriteTests : IAsyncLifetime
{
    private const string Plugins = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!, _cora = null!;
    private string _folder = null!, _page = null!, _file = null!;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe, m => m with { Enforcing = true }));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var cora = await UserAsync(_f, "cora");
        (await _admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = cora.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        _cora = Basic(_f, "cora");

        // A page that gains a child is a folder; it holds one file. Another page is where files move to.
        _folder = await CreateAsync("Guides");
        await CreateAsync("Inside", _folder);
        _page = await CreateAsync("Elsewhere");
        _file = (await (await DcFilesTests.UploadAsync(_cora, $"/rest/api/content/{_folder}/child/attachment", DcFilesTests.Files(("a.txt", "first", null)))).JsonAsync(HttpStatusCode.OK))
            .GetProperty("results")[0].GetProperty("id").GetString()!;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<string> CreateAsync(string title, string? parent = null)
    {
        object page = parent is null
            ? new { type = "page", title, space = new { key = "OPS" }, body = new { storage = new { value = "<p>Body</p>", representation = "storage" } } }
            : new { type = "page", title, space = new { key = "OPS" }, ancestors = new[] { new { id = parent } }, body = new { storage = new { value = "<p>Body</p>", representation = "storage" } } };
        return (await (await _cora.PostJsonAsync("/rest/api/content", page)).JsonAsync(HttpStatusCode.OK)).GetProperty("id").GetString()!;
    }

    private async Task<int> VersionAsync(string id) => (await _cora.GetJsonAsync($"/rest/api/content/{id}?expand=version")).GetProperty("version").GetProperty("number").GetInt32();

    /// <summary>Each write to the folder or its file, with the core action whose hooks and lock see it; with <paramref name="only"/>, those of that action.</summary>
    private async Task<List<(string Write, string Action, HttpResponseMessage Answer)>> WritesAsync(HttpClient c, string? only = null)
    {
        var writes = new List<(string Write, string Action, Func<Task<HttpResponseMessage>> Send)>
        {
            ("folder body", "page.save", async () => await c.PutAsJsonAsync($"/rest/api/content/{_folder}",
                new { type = "page", title = "Guides", version = new { number = await VersionAsync(_folder) + 1 }, body = new { storage = new { value = "<p>new body</p>", representation = "storage" } } }, Ct)),
            ("folder title", "page.save", async () => await c.PutAsJsonAsync($"/rest/api/content/{_folder}",
                new { type = "page", title = "Guides two", version = new { number = await VersionAsync(_folder) + 1 } }, Ct)),
            ("file on the folder", "attachment.add", () => DcFilesTests.UploadAsync(c, $"/rest/api/content/{_folder}/child/attachment", DcFilesTests.Files(("b.txt", "second", null)))),
            ("new version by upload", "attachment.add", () => DcFilesTests.UploadAsync(c, $"/rest/api/content/{_folder}/child/attachment?allowDuplicated=true", DcFilesTests.Files(("a.txt", "again", null)))),
            ("new version by data", "attachment.add", () => DcFilesTests.UploadAsync(c, $"/rest/api/content/{_folder}/child/attachment/{_file}/data", DcFilesTests.Files(("a.txt", "data", null)))),
            ("rename by update", "attachment.add", async () => await c.PutAsJsonAsync($"/rest/api/content/{_folder}/child/attachment/{_file}",
                new { type = "attachment", title = "renamed.txt", version = new { number = await VersionAsync(_file) + 1 } }, Ct)),
            ("move", "attachment.add", () => c.PostAsync($"/rest/api/content/{_folder}/child/attachment/{_file}/move?newContentId={_page}", null, Ct)),
        };
        var answers = new List<(string, string, HttpResponseMessage)>();
        foreach (var (write, action, send) in writes.Where(w => only is null || w.Action == only)) answers.Add((write, action, await send()));
        return answers;
    }

    private async Task RefuseAsync(string action) =>
        (await _admin.PutAsJsonAsync($"{Plugins}/kb.test.probe/settings", new { values = new Dictionary<string, string> { ["Refuse"] = action } }, Ct)).EnsureSuccessStatusCode();

    [Fact]
    public async Task APluginsHook_SeesEachWrite_AndItsRefusalHolds()
    {
        foreach (var action in new[] { "page.save", "attachment.add" })
        {
            await RefuseAsync(action);
            // The surface answers a plugin's refusal as its errors are shaped: 403, with the plugin's words.
            foreach (var (write, _, answer) in await WritesAsync(_cora, action))
                Assert.True(!answer.IsSuccessStatusCode && (await answer.Content.ReadAsStringAsync(Ct)).Contains("Probe refused.", StringComparison.Ordinal),
                    $"{write}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync(Ct)}");
        }

        // Nothing was written: the folder's body and title, and the file's name, place and content are as they were.
        var folder = await _cora.GetJsonAsync($"/rest/api/content/{_folder}?expand=body.storage");
        Assert.Equal(("Guides", "<p>Body</p>"), (folder.GetProperty("title").GetString(), folder.GetProperty("body").GetProperty("storage").GetProperty("value").GetString()));
        var files = await _cora.GetJsonAsync($"/rest/api/content/{_folder}/child/attachment");
        Assert.Equal(["a.txt"], files.GetProperty("results").EnumerateArray().Select(f => f.GetProperty("title").GetString()));
        Assert.Equal(1, await VersionAsync(_file));

        // With nothing refused, every write goes through.
        await RefuseAsync("");
        Assert.All(await WritesAsync(_cora), w => Assert.True(w.Answer.IsSuccessStatusCode, $"{w.Write}: {(int)w.Answer.StatusCode}"));
        var after = await _cora.GetJsonAsync($"/rest/api/content/{_folder}?expand=body.storage");
        Assert.Equal(("Guides two", "<p>new body</p>"), (after.GetProperty("title").GetString(), after.GetProperty("body").GetProperty("storage").GetProperty("value").GetString()));
    }

    [Fact]
    public async Task AnEnforcingPluginsLock_HoldsEachWrite_ForEveryoneButAnAdmin()
    {
        // The probe fails at the next start, so the core disables it; it is enforcing, so what it hooked is locked.
        (await _admin.PutAsJsonAsync($"{Plugins}/kb.test.probe/settings", new { values = new Dictionary<string, string> { ["FailOn"] = "configure" } }, Ct)).EnsureSuccessStatusCode();
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _cora = Basic(_f, "cora");
        await _f.WithDbAsync(async db => Assert.NotNull((await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct)).LockedAt));

        foreach (var (write, _, answer) in await WritesAsync(_cora))
            Assert.True(!answer.IsSuccessStatusCode && (await answer.Content.ReadAsStringAsync(Ct)).Contains("Test probe is disabled.", StringComparison.Ordinal),
                $"{write}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal("<p>Body</p>", (await _cora.GetJsonAsync($"/rest/api/content/{_folder}?expand=body.storage")).GetProperty("body").GetProperty("storage").GetProperty("value").GetString());
    }
}
