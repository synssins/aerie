using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The version and draft routes go through the action bus: each answers with its action's name, a plugin can refuse and shape them, and the checks answer as before.</summary>
public sealed class VersionPipelineTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static string Doc(string text) => JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } } });

    /// <summary>A page at 1.1: "Step one" in 1.0, "Step 1" in 1.1.</summary>
    private async Task<PageDto> PageAsync()
    {
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Page", title = "Runbook", contentJson = Doc("Step one") }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;
        return (await (await _admin.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { title = "Runbook", contentJson = Doc("Step 1"), baseVersion = "1.0" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;
    }

    private async Task<PageDto> ReadAsync(Guid id) => (await _admin.GetFromJsonAsync<PageDto>($"/api/v1/pages/{id}", Ct))!;
    private static Task<HttpResponseMessage> DraftAsync(HttpClient client, Guid id) =>
        client.PutAsJsonAsync($"/api/v1/pages/{id}/draft", new { title = "Runbook", contentJson = Doc("unsaved"), baseVersion = "1.1", tags = new[] { "How-To" } }, Ct);

    /// <summary>A signed-in person who is not an Admin, with that role in the space (none when null).</summary>
    private async Task<HttpClient> UserAsync(string name, string? role)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        if (role is not null)
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{_space}/roles", new { principalType = "User", principalId = id.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        var client = _f.NewClient();
        (await client.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private async Task<List<(Guid? Actor, string? TargetType, string? TargetId, Guid? Space, string Data)>> RestoresAsync()
    {
        List<(Guid?, string?, string?, Guid?, string)> written = [];
        await _f.WithDbAsync(async db => written = [.. (await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PageVersionRestore).OrderBy(a => a.Sequence).ToListAsync(Ct))
            .Select(a => (a.ActorId, a.TargetType, a.TargetId, a.SpaceId, a.DataJson))]);
        return written;
    }

    [Fact]
    public async Task EveryVersionRoute_AnswersWithItsActionName()
    {
        var page = await PageAsync();
        var at = $"/api/v1/pages/{page.Id}";

        var list = await _admin.GetAsync($"{at}/versions", Ct);
        Assert.Equal((HttpStatusCode.OK, "page.version.list"), (list.StatusCode, Action(list)));
        Assert.Equal(["1.1", "1.0"], (await list.Content.ReadFromJsonAsync<List<VersionDto>>(Ct))!.Select(v => v.Version));

        var one = await _admin.GetAsync($"{at}/versions/1.0", Ct);
        Assert.Equal((HttpStatusCode.OK, "page.version.read"), (one.StatusCode, Action(one)));
        var old = (await one.Content.ReadFromJsonAsync<JsonElement>(Ct));
        Assert.Equal(("1.0", "Runbook", Doc("Step one"), "Break-glass administrator"), (old.GetProperty("version").GetString(), old.GetProperty("title").GetString(), old.GetProperty("contentJson").GetString(), old.GetProperty("author").GetString()));
        Assert.True(old.TryGetProperty("at", out _));

        var diff = await _admin.GetAsync($"{at}/diff?from=1.0&to=1.1", Ct);
        Assert.Equal((HttpStatusCode.OK, "page.diff"), (diff.StatusCode, Action(diff)));
        var lines = (await diff.Content.ReadFromJsonAsync<JsonElement>(Ct));
        Assert.Equal(("1.0", "1.1"), (lines.GetProperty("from").GetString(), lines.GetProperty("to").GetString()));
        Assert.Contains(lines.GetProperty("lines").EnumerateArray().ToList(), l => l.GetProperty("text").GetString() == "Step one");
        // Both ends are needed, as before.
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync($"{at}/diff?from=1.0", Ct)).StatusCode);

        var restored = await _admin.PostAsync($"{at}/versions/1.0/restore", null, Ct);
        Assert.Equal((HttpStatusCode.OK, "page.version.restore"), (restored.StatusCode, Action(restored)));
        var now = (await restored.Content.ReadFromJsonAsync<PageDto>(Ct))!;
        Assert.Equal(("1.2", Doc("Step one"), "Restored from 1.0"), (now.Version, now.ContentJson, now.History[0].Note));

        var saved = await DraftAsync(_admin, page.Id);
        Assert.Equal((HttpStatusCode.OK, "draft.save"), (saved.StatusCode, Action(saved)));
        var updatedAt = Assert.Single((await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().ToList());
        Assert.Equal("updatedAt", updatedAt.Name);

        var draft = await _admin.GetAsync($"{at}/draft", Ct);
        Assert.Equal((HttpStatusCode.OK, "draft.read"), (draft.StatusCode, Action(draft)));
        var mine = (await draft.Content.ReadFromJsonAsync<JsonElement>(Ct));
        Assert.Equal(("Runbook", Doc("unsaved"), "1.1"), (mine.GetProperty("title").GetString(), mine.GetProperty("contentJson").GetString(), mine.GetProperty("baseVersion").GetString()));
        Assert.True(mine.TryGetProperty("updatedAt", out _));
        Assert.Equal(["how-to"], mine.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        var discarded = await _admin.DeleteAsync($"{at}/draft", Ct);
        Assert.Equal((HttpStatusCode.NoContent, "draft.discard"), (discarded.StatusCode, Action(discarded)));
        // A refusal by the core names the action too.
        var gone = await _admin.GetAsync($"{at}/draft", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "draft.read"), (gone.StatusCode, Action(gone)));
        var missing = await _admin.GetAsync($"{at}/versions/9.9", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "page.version.read"), (missing.StatusCode, Action(missing)));

        // The restore is in the audit log once, under today's name and data, done by the caller. Nothing else here is audited.
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        var entry = Assert.Single(await RestoresAsync());
        Assert.Equal((me, "page", page.Id.ToString(), _space), (entry.Actor, entry.TargetType, entry.TargetId, entry.Space));
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.Data)!;
        Assert.Equal(("1.0", "1.2", 2), (data["from"], data["to"], data.Count));
    }

    [Fact]
    public async Task Restore_RefusedByHook_Is422_PageUnchanged()
    {
        var page = await PageAsync();
        await SetAsync("Refuse", "page.version.restore");

        var r = await _admin.PostAsync($"/api/v1/pages/{page.Id}/versions/1.0/restore", null, Ct);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        var after = await ReadAsync(page.Id);
        Assert.Equal(("1.1", Doc("Step 1")), (after.Version, after.ContentJson));
        Assert.Empty(await RestoresAsync());
    }

    [Fact]
    public async Task SaveDraft_RefusedByHook_Is422_NoDraftIsKept()
    {
        var page = await PageAsync();
        await SetAsync("Refuse", "draft.save");

        var r = await DraftAsync(_admin, page.Id);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/v1/pages/{page.Id}/draft", Ct)).StatusCode);
    }

    [Fact]
    public async Task ReadVersion_FilterTrimsTheResult()
    {
        var page = await PageAsync();
        await SetAsync("Blank", "page.version.read:Title");

        Assert.Equal("", (await _admin.GetFromJsonAsync<VersionContentDto>($"/api/v1/pages/{page.Id}/versions/1.0", Ct))!.Title);
        // The filter shapes the answer, not the version: restoring it still brings the title back.
        var restored = await (await _admin.PostAsync($"/api/v1/pages/{page.Id}/versions/1.0/restore", null, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct);
        Assert.Equal("Runbook", restored!.Title);
    }

    /// <summary>
    /// A restore that loses the race for its version number. The publish answers 409, and the restore still asks for its audit entry.
    /// That entry is written through the database session that holds the failed save, so writing it fails too: the answer is 500 and nothing is kept.
    /// It was so before the route went through the bus; this pins that the bus did not change it.
    /// </summary>
    [Fact]
    public async Task Restore_VersionNumberTaken_AnswersAsBefore()
    {
        var page = await PageAsync();
        await _f.WithDbAsync(async db =>
        {
            db.PageVersions.Add(new PageVersion { PageId = page.Id, Major = 1, Minor = 2, Title = "Runbook", ContentJson = Doc("Someone else"), AuthorId = Guid.NewGuid(), At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        var r = await _admin.PostAsync($"/api/v1/pages/{page.Id}/versions/1.0/restore", null, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var after = await ReadAsync(page.Id);
        Assert.Equal(("1.1", Doc("Step 1")), (after.Version, after.ContentJson));
        Assert.Empty(await RestoresAsync());
    }

    [Fact]
    public async Task Checks_AnswerAsBefore()
    {
        var page = await PageAsync();
        var at = $"/api/v1/pages/{page.Id}";
        var outsider = await UserAsync("oscar", null);
        var reader = await UserAsync("rita", "Reader");
        var folder = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{_space}/pages", new { kind = "Folder", title = "How-to" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!;

        // No role in the space: the page does not exist.
        foreach (var path in new[] { "/versions", "/versions/1.0", "/diff?from=1.0&to=1.1" })
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(at + path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"{at}/versions/1.0/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DraftAsync(outsider, page.Id)).StatusCode);

        // A Reader reads the history and may not write. 403 comes before the version is looked for.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{at}/versions/1.0", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{at}/versions/9.9/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(reader, page.Id)).StatusCode);

        // An editor: a version that is not there, a folder, a draft that is not one.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"{at}/versions/9.9/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsync($"{at}/versions/latest/restore", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{at}/diff?from=1.0&to=9.9", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DraftAsync(_admin, folder.Id)).StatusCode);
        var bad = await _admin.PutAsJsonAsync($"{at}/draft", new { title = "Runbook", contentJson = "{}", baseVersion = "1.1" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("Invalid draft.", (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("draft")[0].GetString());
        var tags = await _admin.PutAsJsonAsync($"{at}/draft", new { title = "Runbook", contentJson = Doc("x"), baseVersion = "1.1", tags = new[] { "a,b" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tags.StatusCode);
        Assert.True((await tags.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("tags", out _));

        // A draft belongs to its author: reading and discarding ask for no role, and reach nobody else's draft.
        Assert.Equal(HttpStatusCode.OK, (await DraftAsync(_admin, page.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{at}/draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await outsider.DeleteAsync($"{at}/draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync($"/api/v1/pages/{Guid.NewGuid()}/draft", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"{at}/draft", Ct)).StatusCode);
        // Someone else's fresh draft is not taken over.
        var contributor = await UserAsync("dana", "Contributor");
        Assert.Equal(HttpStatusCode.Conflict, (await DraftAsync(contributor, page.Id)).StatusCode);
    }
}
