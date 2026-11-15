using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>The five user operations (contracts/behaviour/users.md).</summary>
public sealed class DcUserTests : IAsyncLifetime
{
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>An Admin, an editor (who may look people up), a reader (who may not) and the reader's group.</summary>
    private async Task<(HttpClient Admin, Guid Editor, Guid Reader)> PeopleAsync()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var editor = await UserAsync(_f, "editor");
        var reader = await UserAsync(_f, "reader");
        (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = editor.ToString(), role = "Contributor" }, Ct)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = reader.ToString(), role = "Reader" }, Ct)).EnsureSuccessStatusCode();
        await DcPlumbingTests.SetGroupAsync(_f, "readers", reader);
        await DcPlumbingTests.SetGroupAsync(_f, "auditors", reader);
        return (admin, editor, reader);
    }

    [Fact]
    public async Task Anonymous_IsTheAnonymousUser()
    {
        foreach (var path in new[] { "/rest/api/user/anonymous", "/rest/api/user/current" })
        {
            var u = await _f.NewClient().GetJsonAsync(path);
            Assert.Equal("anonymous", u.GetProperty("type").GetString());
            Assert.Equal("Anonymous", u.GetProperty("displayName").GetString());
            Assert.False(u.TryGetProperty("username", out _));
            Assert.False(u.TryGetProperty("userKey", out _));
            Assert.True(u.GetProperty("profilePicture").GetProperty("isDefault").GetBoolean());
            Assert.Equal("https://localhost", u.GetProperty("_links").GetProperty("base").GetString());
            Assert.Equal("", u.GetProperty("_links").GetProperty("context").GetString());
        }
    }

    [Fact]
    public async Task Current_ExpandsStatus_OnlyWhenAsked()
    {
        var (_, editor, _) = await PeopleAsync();
        var plain = await Basic(_f, "editor").GetJsonAsync("/rest/api/user/current");
        Assert.False(plain.TryGetProperty("status", out _));
        Assert.Equal("", plain.GetProperty("_expandable").GetProperty("status").GetString());
        Assert.Equal($"https://localhost/rest/api/user?key={editor:N}", plain.GetProperty("_links").GetProperty("self").GetString());
        var expanded = await Basic(_f, "editor").GetJsonAsync("/rest/api/user/current?expand=status");
        Assert.Equal("current", expanded.GetProperty("status").GetString());
        Assert.False(expanded.TryGetProperty("_expandable", out _));
    }

    [Fact]
    public async Task GetUser_ByKeyOrName_WithTheBrowseRule()
    {
        var (admin, editor, reader) = await PeopleAsync();
        await (await _f.NewClient().GetAsync($"/rest/api/user?key={editor:N}", Ct)).ErrorAsync(HttpStatusCode.Forbidden);

        var byName = await Basic(_f, "editor").GetJsonAsync("/rest/api/user?username=READER");
        Assert.Equal(reader.ToString("N"), byName.GetProperty("userKey").GetString());
        Assert.Equal("reader", byName.GetProperty("username").GetString());
        Assert.Equal("reader Person", byName.GetProperty("displayName").GetString());
        var byKey = await admin.GetJsonAsync($"/rest/api/user?key={editor:N}");
        Assert.Equal("editor", byKey.GetProperty("username").GetString());

        // A reader may not look others up, but may read their own record.
        await (await Basic(_f, "reader").GetAsync($"/rest/api/user?key={editor:N}", Ct)).ErrorAsync(HttpStatusCode.Forbidden);
        Assert.Equal("reader", (await Basic(_f, "reader").GetJsonAsync("/rest/api/user?username=reader")).GetProperty("username").GetString());

        var e = Basic(_f, "editor");
        await (await e.GetAsync("/rest/api/user", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await e.GetAsync($"/rest/api/user?key={editor:N}&username=editor", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await e.GetAsync($"/rest/api/user?key={Guid.NewGuid():N}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await e.GetAsync("/rest/api/user?key=not-a-key", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await e.GetAsync("/rest/api/user?username=breakglass", Ct)).ErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUser_DeactivatedShowsDeactivated_DeletedIsGone()
    {
        var (admin, _, _) = await PeopleAsync();
        var gone = await UserAsync(_f, "gone");
        var off = await UserAsync(_f, "off");
        await _f.WithDbAsync(async db =>
        {
            await db.Users.Where(u => u.Id == gone).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow).SetProperty(u => u.DeactivatedAt, DateTimeOffset.UtcNow), Ct);
            await db.Users.Where(u => u.Id == off).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivatedAt, DateTimeOffset.UtcNow), Ct);
        });
        await (await admin.GetAsync($"/rest/api/user?key={gone:N}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        Assert.Equal("deactivated", (await admin.GetJsonAsync($"/rest/api/user?key={off:N}&expand=status")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_NeedsTheBrowseRule_AndPages()
    {
        var (admin, _, _) = await PeopleAsync();
        await UserAsync(_f, "zed");
        await (await _f.NewClient().GetAsync("/rest/api/user/list", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await Basic(_f, "reader").GetAsync("/rest/api/user/list", Ct)).ErrorAsync(HttpStatusCode.Forbidden);

        var all = await Basic(_f, "editor").GetJsonAsync("/rest/api/user/list");
        Assert.Equal(["editor", "reader", "zed"], all.GetProperty("results").EnumerateArray().Select(u => u.GetProperty("username").GetString()));
        Assert.Equal(100, all.GetProperty("limit").GetInt32());
        Assert.Equal(3, all.GetProperty("size").GetInt32());

        var first = await admin.GetJsonAsync("/rest/api/user/list?limit=2");
        Assert.Equal(2, first.GetProperty("size").GetInt32());
        var next = first.GetProperty("_links").GetProperty("next").GetString()!;
        Assert.Equal("/rest/api/user/list?limit=2&start=2", next);
        Assert.False(first.GetProperty("_links").TryGetProperty("prev", out _));
        var second = await admin.GetJsonAsync(next);
        Assert.Equal("zed", second.GetProperty("results")[0].GetProperty("username").GetString());
        Assert.Equal("/rest/api/user/list?limit=2&start=0", second.GetProperty("_links").GetProperty("prev").GetString());
        Assert.False(second.GetProperty("_links").TryGetProperty("next", out _));

        Assert.Equal(200, (await admin.GetJsonAsync("/rest/api/user/list?limit=5000")).GetProperty("limit").GetInt32());
        await (await admin.GetAsync("/rest/api/user/list?limit=0", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await admin.GetAsync("/rest/api/user/list?start=-1", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await admin.GetAsync("/rest/api/user/list?start=x", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MemberOf_OwnGroupsOrAnAdmins_Paged()
    {
        var (admin, _, reader) = await PeopleAsync();
        await (await _f.NewClient().GetAsync($"/rest/api/user/memberof?key={reader:N}", Ct)).ErrorAsync(HttpStatusCode.Unauthorized);
        await (await Basic(_f, "editor").GetAsync($"/rest/api/user/memberof?key={reader:N}", Ct)).ErrorAsync(HttpStatusCode.Forbidden);

        var own = await Basic(_f, "reader").GetJsonAsync("/rest/api/user/memberof?username=reader");
        Assert.Equal(200, own.GetProperty("limit").GetInt32());
        Assert.Equal(["auditors", "readers"], own.GetProperty("results").EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        Assert.All(own.GetProperty("results").EnumerateArray(), g => Assert.Equal("group", g.GetProperty("type").GetString()));

        var page = await admin.GetJsonAsync($"/rest/api/user/memberof?key={reader:N}&limit=1&start=1");
        Assert.Equal("readers", page.GetProperty("results")[0].GetProperty("name").GetString());
        Assert.True(page.GetProperty("_links").TryGetProperty("prev", out _));

        await (await admin.GetAsync($"/rest/api/user/memberof?key={Guid.NewGuid():N}", Ct)).ErrorAsync(HttpStatusCode.NotFound);
        await (await admin.GetAsync("/rest/api/user/memberof", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
        await (await admin.GetAsync($"/rest/api/user/memberof?key={reader:N}&limit=0", Ct)).ErrorAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnswersSayWhichActionRan()
    {
        var (admin, _, _) = await PeopleAsync();
        var r = await admin.GetAsync("/rest/api/user/list", Ct);
        Assert.Equal("dc.user.list", r.Headers.GetValues("X-Kb-Action").Single());
    }
}
