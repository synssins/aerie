using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>Spaces and long tasks (contracts/behaviour/spaces.md, common.md).</summary>
public sealed class DcSpaceTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await _f.SignedInBreakGlassAsync();
        _admin.DefaultRequestHeaders.Add("X-Atlassian-Token", "no-check"); // a cookie caller's writes need it (identity.md, Sessions)
    }
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>A space made through aerie's own API, with these people in it (role by name).</summary>
    private async Task<Guid> SpaceAsync(string key, string name, params (string User, string Role)[] members)
    {
        var id = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name, key }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var (user, role) in members)
        {
            var uid = await UserIdAsync(_f, user) ?? await UserAsync(_f, user);
            (await _admin.PutAsJsonAsync($"/api/v1/spaces/{id}/roles", new { principalType = "User", principalId = uid.ToString(), role }, Ct)).EnsureSuccessStatusCode();
        }
        return id;
    }

    private static JsonElement Results(JsonElement page) => page.GetProperty("results");
    private static IEnumerable<string?> Keys(JsonElement page) => Results(page).EnumerateArray().Select(s => s.GetProperty("key").GetString());

    [Fact]
    public async Task Create_MapsTheKey_AndMakesNoPage()
    {
        await (await _f.NewClient().PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" })).ErrorAsync(HttpStatusCode.Unauthorized);
        await UserAsync(_f, "ravi");
        await (await Basic(_f, "ravi").PostJsonAsync("/rest/api/space", new { key = "LAB", name = "Lab" })).ErrorAsync(HttpStatusCode.Forbidden);

        var s = await (await _admin.PostJsonAsync("/rest/api/space", new { key = "Lab", name = "Lab", description = new { plain = new { value = "Bench <notes>", representation = "plain" } }, homepage = new { id = "1" } }))
            .JsonAsync(HttpStatusCode.OK);
        Assert.Equal("LAB", s.GetProperty("key").GetString());
        Assert.Equal("global", s.GetProperty("type").GetString());
        Assert.Equal("current", s.GetProperty("status").GetString());
        Assert.True(s.GetProperty("id").GetInt64() > 0);
        Assert.Equal("https://localhost/rest/api/space/LAB", s.GetProperty("_links").GetProperty("self").GetString());
        Assert.Equal("/rest/api/space", s.GetProperty("_links").GetProperty("collection").GetString());
        Assert.False(s.TryGetProperty("homepage", out _));
        Assert.False(s.GetProperty("_expandable").TryGetProperty("homepage", out _));
        await _f.WithDbAsync(async db =>
        {
            var space = await db.Spaces.SingleAsync(x => x.Key == "lab", Ct);
            Assert.Equal("Bench <notes>", space.Description);
            Assert.False(await db.Pages.AnyAsync(p => p.SpaceId == space.Id, Ct));
        });

        // Keys compare without regard to case; aerie's own key rules apply.
        await (await _admin.PostJsonAsync("/rest/api/space", new { key = "lab", name = "Again" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _admin.PostJsonAsync("/rest/api/space", new { key = "A_B", name = "Bad" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _admin.PostJsonAsync("/rest/api/space", new { key = new string('K', 33), name = "Long" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _admin.PostJsonAsync("/rest/api/space", new { key = "OK2" })).ErrorAsync(HttpStatusCode.BadRequest);
        await (await _admin.PostAsync("/rest/api/space", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"), Ct)).ErrorAsync(HttpStatusCode.BadRequest);

        var p = await (await _admin.PostJsonAsync("/rest/api/space/_private", new { key = "team-a", name = "Team A" })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("TEAM-A", p.GetProperty("key").GetString());
        // spaces.md: a global space is read by everyone signed in; a _private one only by those given a role.
        Assert.Equal(["LAB"], Keys(await Basic(_f, "ravi").GetJsonAsync("/rest/api/space")));
        await (await Basic(_f, "ravi").GetAsync("/rest/api/space/TEAM-A", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().GetAsync("/rest/api/space/LAB", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await _f.WithDbAsync(async db => Assert.Equal((Kb.Plugins.SpaceVisibility.SignedIn, Kb.Plugins.SpaceVisibility.Private),
            ((await db.Spaces.SingleAsync(x => x.Key == "lab", Ct)).Visibility, (await db.Spaces.SingleAsync(x => x.Key == "team-a", Ct)).Visibility)));
    }

    [Fact]
    public async Task Read_AnyCase_404WithoutARole_AndExpands()
    {
        await SpaceAsync("ops", "Operations", ("rita", "Reader"));
        await UserAsync(_f, "out");
        var rita = Basic(_f, "rita");
        var s = await rita.GetJsonAsync("/rest/api/space/oPs");
        Assert.Equal("OPS", s.GetProperty("key").GetString());
        Assert.Equal("https://localhost", s.GetProperty("_links").GetProperty("base").GetString());
        var expandable = s.GetProperty("_expandable");
        foreach (var name in new[] { "description", "icon", "metadata", "creationDate", "retentionPolicy", "permissions" }) Assert.True(expandable.TryGetProperty(name, out _), name);
        Assert.False(s.TryGetProperty("description", out _));

        var x = await rita.GetJsonAsync("/rest/api/space/OPS?expand=description.plain,icon,metadata.labels,creationDate,homepage.body.storage");
        Assert.Equal("plain", x.GetProperty("description").GetProperty("plain").GetProperty("representation").GetString());
        Assert.False(x.GetProperty("description").TryGetProperty("view", out _));
        Assert.True(x.GetProperty("icon").GetProperty("isDefault").GetBoolean());
        Assert.Equal(0, x.GetProperty("metadata").GetProperty("labels").GetProperty("size").GetInt32());
        Assert.True(DateTimeOffset.TryParse(x.GetProperty("creationDate").GetString(), out _));
        Assert.False(x.TryGetProperty("homepage", out _));

        await (await Basic(_f, "out").GetAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await rita.GetAsync("/rest/api/space/NOPE", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().GetAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ids_AreGivenInCreationOrder_AndNeverChange()
    {
        await SpaceAsync("first", "First");
        await SpaceAsync("second", "Second");
        var a = (await _admin.GetJsonAsync("/rest/api/space/SECOND")).GetProperty("id").GetInt64();
        var b = (await _admin.GetJsonAsync("/rest/api/space/FIRST")).GetProperty("id").GetInt64();
        Assert.True(b < a);
        await (await _admin.PutAsJsonAsync("/rest/api/space/FIRST", new { key = "FIRST", name = "Renamed" }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(b, (await _admin.GetJsonAsync("/rest/api/space/first")).GetProperty("id").GetInt64());
        Assert.Equal(["FIRST"], Keys(await _admin.GetJsonAsync($"/rest/api/space?spaceId={b}")));
    }

    [Fact]
    public async Task List_Filters_Orders_AndPages()
    {
        await SpaceAsync("beta", "Beta", ("rita", "Reader"));
        var alpha = await SpaceAsync("alpha", "Alpha", ("rita", "Owner"));
        await SpaceAsync("gamma", "Gamma", ("rita", "Reader"));
        await SpaceAsync("hidden", "Aaa hidden");
        var rita = Basic(_f, "rita");
        Assert.Equal(["ALPHA", "BETA", "GAMMA"], Keys(await rita.GetJsonAsync("/rest/api/space")));
        Assert.Equal(25, (await rita.GetJsonAsync("/rest/api/space")).GetProperty("limit").GetInt32());
        Assert.Equal(["ALPHA", "GAMMA"], Keys(await rita.GetJsonAsync("/rest/api/space?spaceKey=gamma&spaceKey=ALPHA&spaceKey=hidden")));
        Assert.Equal(["BETA", "GAMMA"], Keys(await rita.GetJsonAsync("/rest/api/space?spaceKeys=beta,Gamma")));
        Assert.Equal(["BETA"], Keys(await rita.GetJsonAsync("/rest/api/space?spaceKeySingle=BETA")));
        await (await rita.GetAsync("/rest/api/space?spaceKey=BETA&spaceId=1", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await rita.GetAsync("/rest/api/space?spaceIds=x", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await rita.GetAsync("/rest/api/space?type=team", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await rita.GetAsync("/rest/api/space?status=gone", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        Assert.Equal(3, Results(await rita.GetJsonAsync("/rest/api/space?type=global")).GetArrayLength());
        Assert.Empty(Keys(await rita.GetJsonAsync("/rest/api/space?type=personal")));
        Assert.Empty(Keys(await rita.GetJsonAsync("/rest/api/space?label=team")));
        Assert.Empty(Keys(await rita.GetJsonAsync("/rest/api/space?favourite=true")));
        Assert.Empty(Keys(await rita.GetJsonAsync("/rest/api/space?hasRetentionPolicy=true")));

        (await rita.PutAsync("/rest/api/space/ALPHA/archive", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(["ALPHA"], Keys(await rita.GetJsonAsync("/rest/api/space?status=archived")));
        Assert.Equal(["BETA", "GAMMA"], Keys(await rita.GetJsonAsync("/rest/api/space?status=current")));

        await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{alpha}/pages", new { kind = "Page", title = "Runbook", tags = new[] { "release" } }, Ct)).JsonAsync(HttpStatusCode.Created);
        Assert.Equal(["ALPHA"], Keys(await rita.GetJsonAsync("/rest/api/space?contentLabel=Release")));

        var first = await rita.GetJsonAsync("/rest/api/space?limit=2");
        Assert.Equal(["ALPHA", "BETA"], Keys(first));
        Assert.Equal("/rest/api/space?limit=2&start=2", first.GetProperty("_links").GetProperty("next").GetString());
        Assert.Equal(["GAMMA"], Keys(await rita.GetJsonAsync("/rest/api/space?limit=2&start=2")));
        await (await rita.GetAsync("/rest/api/space?limit=0", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        Assert.Empty(Keys(await _f.NewClient().GetJsonAsync("/rest/api/space")));
    }

    [Fact]
    public async Task Update_NeedsSpaceAdministration_AndKeepsTheKey()
    {
        await SpaceAsync("ops", "Operations", ("olga", "Owner"), ("rita", "Reader"));
        await UserAsync(_f, "out");
        var olga = Basic(_f, "olga");
        var s = await (await olga.PutAsJsonAsync("/rest/api/space/ops", new { key = "Ops", name = "Operations", description = new { plain = new { value = "Runbooks", representation = "plain" } } }, Ct))
            .JsonAsync(HttpStatusCode.OK);
        Assert.Equal("OPS", s.GetProperty("key").GetString());
        Assert.Equal("Runbooks", (await olga.GetJsonAsync("/rest/api/space/OPS?expand=description.plain")).GetProperty("description").GetProperty("plain").GetProperty("value").GetString());

        // Renaming is an Admin's in aerie.
        await (await olga.PutAsJsonAsync("/rest/api/space/OPS", new { key = "OPS", name = "Ops team" }, Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Ops team", (await (await _admin.PutAsJsonAsync("/rest/api/space/OPS", new { key = "OPS", name = "Ops team" }, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("name").GetString());

        await (await olga.PutAsJsonAsync("/rest/api/space/OPS", new { key = "OTHER", name = "Ops team" }, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await olga.PutAsJsonAsync("/rest/api/space/OPS", new { key = "OPS" }, Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await Basic(_f, "rita").PutAsJsonAsync("/rest/api/space/OPS", new { key = "OPS", name = "Ops team" }, Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await Basic(_f, "out").PutAsJsonAsync("/rest/api/space/OPS", new { key = "OPS", name = "Ops team" }, Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().PutAsJsonAsync("/rest/api/space/OPS", new { key = "OPS", name = "Ops team" }, Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ArchiveAndRestore_AreIdempotent_AndAudited()
    {
        var id = await SpaceAsync("ops", "Operations", ("olga", "Owner"), ("rita", "Reader"));
        var olga = Basic(_f, "olga");
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NoContent, (await olga.PutAsync("/rest/api/space/OPS/archive", null, Ct)).StatusCode);
        Assert.Equal("archived", (await olga.GetJsonAsync("/rest/api/space/OPS")).GetProperty("status").GetString());
        await (await Basic(_f, "rita").PutAsync("/rest/api/space/OPS/restore", null, Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await _f.NewClient().PutAsync("/rest/api/space/OPS/restore", null, Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await olga.PutAsync("/rest/api/space/NOPE/restore", null, Ct)).ErrorAsync(HttpStatusCode.NotFound);
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NoContent, (await olga.PutAsync("/rest/api/space/ops/restore", null, Ct)).StatusCode);
        Assert.Equal("current", (await olga.GetJsonAsync("/rest/api/space/OPS")).GetProperty("status").GetString());
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.SpaceArchive && a.SpaceId == id, Ct));
            Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.SpaceUnarchive && a.SpaceId == id, Ct));
        });
    }

    [Fact]
    public async Task Delete_IsALongTask_AnAdmins()
    {
        await SpaceAsync("ops", "Operations", ("olga", "Owner"));
        await UserAsync(_f, "out");
        await (await Basic(_f, "olga").DeleteAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        await (await _f.NewClient().DeleteAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await _admin.DeleteAsync("/rest/api/space/NOPE", Ct)).ErrorAsync(HttpStatusCode.NotFound);

        var sub = await (await _admin.DeleteAsync("/rest/api/space/ops", Ct)).JsonAsync(HttpStatusCode.Accepted);
        var taskId = sub.GetProperty("id").GetString()!;
        Assert.Equal($"/rest/api/longtask/{taskId}", sub.GetProperty("links").GetProperty("status").GetString());
        await (await _admin.GetAsync("/rest/api/space/OPS", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SpaceDelete, Ct)));

        var task = await _admin.GetJsonAsync($"/rest/api/longtask/{taskId}");
        Assert.Equal(100, task.GetProperty("percentageComplete").GetInt32());
        Assert.True(task.GetProperty("successful").GetBoolean());
        // A message's args is always empty; the values are in translation (common.md, Message objects; Q-0019).
        var name = task.GetProperty("name");
        Assert.Equal(("aerie.longtask.space.delete", 0, "Deleting space OPS"), (name.GetProperty("key").GetString(), name.GetProperty("args").GetArrayLength(), name.GetProperty("translation").GetString()));
        Assert.Equal(0, task.GetProperty("messages").GetArrayLength());
        Assert.True(task.GetProperty("elapsedTime").GetInt64() >= 0);

        var list = await _admin.GetJsonAsync("/rest/api/longtask");
        Assert.All(list.GetProperty("results").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("name").GetProperty("args").GetArrayLength()));
        Assert.Equal(100, list.GetProperty("limit").GetInt32());
        Assert.Equal(taskId, list.GetProperty("results")[0].GetProperty("id").GetString());
        // Someone who did not start it, and is not an Admin, does not see it.
        var outsider = Basic(_f, "out");
        await (await outsider.GetAsync($"/rest/api/longtask/{taskId}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        Assert.Equal(0, (await outsider.GetJsonAsync("/rest/api/longtask")).GetProperty("size").GetInt32());
        await (await _admin.GetAsync("/rest/api/longtask/not-a-task", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await _f.NewClient().GetAsync("/rest/api/longtask", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await outsider.GetAsync("/rest/api/longtask?start=-1", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Personal_AnAdminsOwn_Once()
    {
        await UserAsync(_f, "ravi");
        await (await Basic(_f, "ravi").PostJsonAsync("/rest/api/space/personal", new { isPrivate = true })).ErrorAsync(HttpStatusCode.Forbidden);
        await (await _f.NewClient().PostJsonAsync("/rest/api/space/personal", new { })).ErrorAsync(HttpStatusCode.Unauthorized);
        var s = await (await _admin.PostJsonAsync("/rest/api/space/personal", new { description = new { plain = new { value = "Mine", representation = "plain" } }, @private = true }))
            .JsonAsync(HttpStatusCode.OK);
        Assert.Equal("~breakglass", s.GetProperty("key").GetString());
        Assert.Equal("personal", s.GetProperty("type").GetString());
        Assert.Equal(["~breakglass"], Keys(await _admin.GetJsonAsync("/rest/api/space?type=personal")));
        Assert.Equal("Mine", (await _admin.GetJsonAsync("/rest/api/space/~BREAKGLASS?expand=description.plain")).GetProperty("description").GetProperty("plain").GetProperty("value").GetString());
        await (await _admin.PostJsonAsync("/rest/api/space/personal", new { })).ErrorAsync(HttpStatusCode.Conflict);
    }
}
