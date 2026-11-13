using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The home and announcement routes go through the action bus: each answers with its action's name, a plugin can refuse a post, and the handlers check the caller.</summary>
public sealed class HomePipelineTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    /// <summary>A signed-in person who is not an Admin and may not announce, and their id.</summary>
    private async Task<(HttpClient Client, Guid Id)> UserAsync(string name)
    {
        Guid id;
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        var client = _f.NewClient();
        (await client.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = Password }, Ct)).EnsureSuccessStatusCode();
        return (client, id);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient c, object body) => c.PostAsJsonAsync("/api/v1/announcements", body, Ct);
    private static Task<HttpResponseMessage> RemoveAsync(HttpClient c, Guid id) => c.DeleteAsync($"/api/v1/announcements/{id}", Ct);
    private static Task<HttpResponseMessage> AnnouncerAsync(HttpClient c, Guid user, bool enabled) => c.PutAsJsonAsync($"/api/v1/admin/users/{user}/announcer", new { enabled }, Ct);
    private static async Task<HomeDto> HomeAsync(HttpClient c) => (await c.GetFromJsonAsync<HomeDto>("/api/v1/home", Ct))!;
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    private static async Task<Guid> IdAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("id").GetGuid();
    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    /// <summary>The audit entries of this slice, oldest first, each as one line.</summary>
    private async Task<List<string>> AuditAsync()
    {
        string[] names = [AuditActions.AnnouncementPost, AuditActions.AnnouncementRemove, AuditActions.AnnouncerSet];
        var lines = new List<string>();
        await _f.WithDbAsync(async db => lines.AddRange((await db.AuditEntries.AsNoTracking().Where(a => names.Contains(a.Action)).OrderBy(a => a.Sequence).ToListAsync(Ct))
            .Select(a => $"{a.Action}|{a.ActorId}|{a.TargetType}|{a.TargetId}|{a.SpaceId}|{a.DataJson}")));
        return lines;
    }

    [Fact]
    public async Task EveryHomeRoute_AnswersWithItsActionName()
    {
        var (_, tech) = await UserAsync("tech1");

        var posted = await PostAsync(_admin, new { title = " Outage ", body = "Email is down." });
        Assert.Equal((HttpStatusCode.Created, "announcement.post"), (posted.StatusCode, Action(posted)));
        var made = await JsonAsync(posted);
        Assert.Equal(["id"], made.EnumerateObject().Select(p => p.Name));
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/announcements/{id}", posted.Headers.Location?.ToString());

        var home = await _admin.GetAsync("/api/v1/home", Ct);
        Assert.Equal((HttpStatusCode.OK, "home.read"), (home.StatusCode, Action(home)));
        var shown = await JsonAsync(home);
        Assert.Equal(["announcements", "recent"], shown.EnumerateObject().Select(p => p.Name));
        var announcement = Assert.Single(shown.GetProperty("announcements").EnumerateArray().ToList());
        Assert.Equal(["id", "title", "body", "by", "createdAt", "expiresAt"], announcement.EnumerateObject().Select(p => p.Name));
        Assert.Equal((id, "Outage", "Email is down."), (announcement.GetProperty("id").GetGuid(), announcement.GetProperty("title").GetString(), announcement.GetProperty("body").GetString()));

        var set = await AnnouncerAsync(_admin, tech, true);
        Assert.Equal((HttpStatusCode.NoContent, "user.announcer.set"), (set.StatusCode, Action(set)));
        var removed = await RemoveAsync(_admin, id);
        Assert.Equal((HttpStatusCode.NoContent, "announcement.remove"), (removed.StatusCode, Action(removed)));
        Assert.Empty((await HomeAsync(_admin)).Announcements);

        // A refusal by the core names the action too, and answers what it always did.
        var again = await RemoveAsync(_admin, id);
        Assert.Equal((HttpStatusCode.NotFound, "announcement.remove"), (again.StatusCode, Action(again)));
        var noUser = await AnnouncerAsync(_admin, Guid.NewGuid(), true);
        Assert.Equal((HttpStatusCode.NotFound, "user.announcer.set"), (noUser.StatusCode, Action(noUser)));
        foreach (var body in new object[] { new { }, new { title = " " }, new { title = new string('x', 121) }, new { title = "Outage", body = new string('x', 2001) }, new { title = "Outage", expiresAt = DateTimeOffset.UtcNow.AddDays(-1) } })
        {
            var bad = await PostAsync(_admin, body);
            Assert.Equal((HttpStatusCode.BadRequest, "announcement.post"), (bad.StatusCode, Action(bad)));
            Assert.Equal("Title 1-120 characters, text up to 2,000, end date in the future.", (await JsonAsync(bad)).GetProperty("errors").GetProperty("announcement")[0].GetString());
        }

        // Each write is in the audit log once, under today's name and data, done by the caller. What was refused is not there.
        var me = (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        Assert.Equal(
        [
            $$"""announcement.post|{{me}}|announcement|{{id}}||{"title":"Outage"}""",
            $$"""user.announcer.set|{{me}}|user|{{tech}}||{"enabled":"True"}""",
            $$"""announcement.remove|{{me}}|announcement|{{id}}||{"title":"Outage"}""",
        ], await AuditAsync());
    }

    [Fact]
    public async Task Post_RefusedByHook_Is422_NothingPosted()
    {
        await SetAsync("Refuse", "announcement.post");

        var r = await PostAsync(_admin, new { title = "Outage" });

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await JsonAsync(r)).GetProperty("detail").GetString());
        Assert.Empty((await HomeAsync(_admin)).Announcements);
        await _f.WithDbAsync(async db => Assert.False(await db.Announcements.AnyAsync(Ct)));
        Assert.Empty(await AuditAsync());
    }

    [Fact]
    public async Task AnnouncerRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        var (user, id) = await UserAsync("u1");

        // The handler refuses, not a filter in front of it: the answer names the action.
        var r = await AnnouncerAsync(user, id, true);
        Assert.Equal((HttpStatusCode.Forbidden, "user.announcer.set"), (r.StatusCode, Action(r)));
        // 403 before 404: a user that does not exist is not told apart.
        Assert.Equal(HttpStatusCode.Forbidden, (await AnnouncerAsync(user, Guid.NewGuid(), true)).StatusCode);
        await _f.WithDbAsync(async db => Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct)).CanAnnounce));
        Assert.Empty(await AuditAsync());
    }

    [Fact]
    public async Task Announcements_AreForAnnouncersAndAdmins_AndAuditedAsTheCaller()
    {
        var (user, id) = await UserAsync("u1");
        var byAdmin = await IdAsync(await PostAsync(_admin, new { title = "Outage" }));

        var post = await PostAsync(user, new { title = "Mine" });
        Assert.Equal((HttpStatusCode.Forbidden, "announcement.post"), (post.StatusCode, Action(post)));
        var remove = await RemoveAsync(user, byAdmin);
        Assert.Equal((HttpStatusCode.Forbidden, "announcement.remove"), (remove.StatusCode, Action(remove)));
        // 403 before 404, and before the text is looked at.
        Assert.Equal(HttpStatusCode.Forbidden, (await RemoveAsync(user, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(user, new { title = "" })).StatusCode);

        (await AnnouncerAsync(_admin, id, true)).EnsureSuccessStatusCode();
        var mine = await IdAsync(await PostAsync(user, new { title = "Mine" }));
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(user, byAdmin)).StatusCode);
        Assert.Equal(mine, Assert.Single((await HomeAsync(user)).Announcements).Id);

        (await AnnouncerAsync(_admin, id, false)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(user, new { title = "Again" })).StatusCode);

        var written = await AuditAsync();
        Assert.Contains($$"""announcement.post|{{id}}|announcement|{{mine}}||{"title":"Mine"}""", written);
        Assert.Contains($$"""announcement.remove|{{id}}|announcement|{{byAdmin}}||{"title":"Outage"}""", written);
        Assert.Equal(2, written.Count(a => a.StartsWith("announcement.post|", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Home_ListsOnlyPagesOfTheCallersSpaces()
    {
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!;
        (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space.Id}/pages", new { kind = "Folder", title = "Guides" }, Ct)).EnsureSuccessStatusCode();
        (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space.Id}/pages", new { kind = "Page", title = "Reset password" }, Ct)).EnsureSuccessStatusCode();
        var (user, _) = await UserAsync("u1");

        var recent = Assert.Single((await HomeAsync(_admin)).Recent); // the page, not the folder
        Assert.Equal(("Reset password", space.Id, "Service Desk"), (recent.Title, recent.SpaceId, recent.SpaceName));
        Assert.Empty((await HomeAsync(user)).Recent);
    }
}
