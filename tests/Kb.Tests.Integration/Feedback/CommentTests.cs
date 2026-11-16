using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Feedback;

/// <summary>Slice C1: page comments without moderation (contracts/behaviour/comments.md; questions Q-0022, Q-0023, Q-0027).</summary>
public sealed class CommentTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private FeedbackKit _k = null!;

    public async ValueTask InitializeAsync() =>
        _k = await FeedbackKit.CreateAsync(new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock))));
    public async ValueTask DisposeAsync() => await _k.DisposeAsync();

    private string Comments(Guid? page = null) => $"/api/v1/pages/{page ?? _k.Page}/comments";

    private async Task<JsonElement> PostAsync(HttpClient c, string text, Guid? parent = null, Guid? page = null) =>
        await (await c.PostAsJsonAsync(Comments(page), new { text, parentId = parent }, Ct)).JsonAsync(HttpStatusCode.Created);

    private async Task<JsonElement> ListAsync(HttpClient c, Guid? page = null) => await (await c.GetAsync(Comments(page), Ct)).JsonAsync(HttpStatusCode.OK);

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static List<JsonElement> Top(JsonElement list) => [.. list.GetProperty("comments").EnumerateArray()];

    // comments.reader.create
    [Fact]
    public async Task AReader_Comments_AndEveryReaderSeesIt()
    {
        var c = await PostAsync(_k.Reader, "  Step 3 is out of date.\nIt needs the new port.  ");
        Assert.Equal("visible", c.GetProperty("state").GetString());
        Assert.Equal("Step 3 is out of date.\nIt needs the new port.", c.GetProperty("text").GetString()); // trimmed, line breaks kept
        Assert.Equal(_k.Ids["rita"].ToString(), c.GetProperty("author").GetProperty("id").GetString());
        Assert.Equal("user", c.GetProperty("author").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("parentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("editedAt").ValueKind);
        Assert.True(c.GetProperty("canEdit").GetBoolean());
        Assert.True(c.GetProperty("canDelete").GetBoolean());
        Assert.False(c.GetProperty("canModerate").GetBoolean());

        var list = await ListAsync(_k.Reader2);
        Assert.True(list.GetProperty("enabled").GetBoolean());
        Assert.True(list.GetProperty("canComment").GetBoolean());
        Assert.False(list.GetProperty("canModerate").GetBoolean());
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var seen = Assert.Single(Top(list));
        Assert.Equal(Id(c), Id(seen));
        Assert.False(seen.GetProperty("canEdit").GetBoolean());
        Assert.False(seen.GetProperty("canDelete").GetBoolean());
        Assert.True((await ListAsync(_k.Supervisor)).GetProperty("canModerate").GetBoolean());
        Assert.False((await ListAsync(_k.Admin)).GetProperty("canModerate").GetBoolean()); // an Admin without a role (Q-0024)
    }

    // comments.guest.refused, comments.unreadable.hidden, comments.folder.refused
    [Fact]
    public async Task Guests_Outsiders_AndFolders_AreRefused()
    {
        var open = await _k.SpaceAsync("Open", "open", "Anyone");
        var page = await _k.PageAsync(open, "Public page");
        var guest = _k.F.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(Comments(page), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync(Comments(page), new { text = "hi" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.GetAsync(Comments(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.PostAsJsonAsync(Comments(), new { text = "hi" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Reader.PostAsJsonAsync(Comments(_k.Folder), new { text = "hi" }, Ct)).StatusCode);
    }

    // comments.text.limits
    [Fact]
    public async Task Text_IsOneTo4000Characters_AfterTrimming()
    {
        foreach (var bad in new[] { "   \n ", new string('a', 4001) })
            Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader.PostAsJsonAsync(Comments(), new { text = bad }, Ct)).StatusCode);
        await PostAsync(_k.Reader, new string('a', 4000));
    }

    // comments.reply.one_level
    [Fact]
    public async Task Replies_AreOneLevel_ToAVisibleTopLevelComment_OnTheSamePage_AtMost100()
    {
        var top = await PostAsync(_k.Reader, "Question");
        var reply = await PostAsync(_k.Contributor, "Answer", Id(top));
        Assert.Equal(Id(top), reply.GetProperty("parentId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader.PostAsJsonAsync(Comments(), new { text = "deeper", parentId = Id(reply) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader.PostAsJsonAsync(Comments(), new { text = "nowhere", parentId = Guid.NewGuid() }, Ct)).StatusCode);
        var other = await _k.PageAsync(_k.Space, "Other");
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader.PostAsJsonAsync(Comments(other), new { text = "elsewhere", parentId = Id(top) }, Ct)).StatusCode);

        var shown = Assert.Single(Top(await ListAsync(_k.Reader2)));
        Assert.Equal(Id(reply), Id(Assert.Single(shown.GetProperty("replies").EnumerateArray())));

        // The 101st reply is refused.
        await _k.F.WithDbAsync(async db =>
        {
            var parent = Id(top);
            for (var i = 0; i < 99; i++)
                db.Add(new Kb.Core.Content.Comment { PageId = _k.Page, ParentId = parent, AuthorId = _k.Ids["rory"], Text = $"r{i}", CreatedAt = _clock.GetUtcNow() });
            await db.SaveChangesAsync(Ct);
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Reader.PostAsJsonAsync(Comments(), new { text = "one more", parentId = Id(top) }, Ct)).StatusCode);
    }

    // comments.edit.window
    [Fact]
    public async Task OnlyTheAuthor_Edits_Within15Minutes()
    {
        var c = await PostAsync(_k.Reader, "Frist");
        _clock.Advance(TimeSpan.FromMinutes(14));
        var edited = await (await _k.Reader.PutAsJsonAsync($"/api/v1/comments/{Id(c)}", new { text = "First" }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("First", edited.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        foreach (var other in new[] { _k.Reader2, _k.Supervisor, _k.Owner })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/v1/comments/{Id(c)}", new { text = "Mine now" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader.PutAsJsonAsync($"/api/v1/comments/{Id(c)}", new { text = " " }, Ct)).StatusCode);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Reader.PutAsJsonAsync($"/api/v1/comments/{Id(c)}", new { text = "Late" }, Ct)).StatusCode);
        Assert.False((await ListAsync(_k.Reader)).GetProperty("comments")[0].GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.PutAsJsonAsync($"/api/v1/comments/{Id(c)}", new { text = "x" }, Ct)).StatusCode);
    }

    // comments.delete.own, comments.delete.placeholder
    [Fact]
    public async Task TheAuthor_Deletes_ForGood_LeavingAPlaceholderOnlyForReplies()
    {
        var lone = await PostAsync(_k.Reader, "Lone comment");
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Reader2.DeleteAsync($"/api/v1/comments/{Id(lone)}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Reader.DeleteAsync($"/api/v1/comments/{Id(lone)}", Ct)).StatusCode);
        Assert.Empty(Top(await ListAsync(_k.Supervisor)));
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Reader.DeleteAsync($"/api/v1/comments/{Id(lone)}", Ct)).StatusCode);

        var top = await PostAsync(_k.Reader, "Secret detail");
        var reply = await PostAsync(_k.Reader2, "Thanks", Id(top));
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Reader.DeleteAsync($"/api/v1/comments/{Id(top)}", Ct)).StatusCode);
        foreach (var c in new[] { _k.Reader2, _k.Supervisor })
        {
            var p = Assert.Single(Top(await ListAsync(c)));
            Assert.Equal("deleted", p.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("author").ValueKind);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("text").ValueKind);
            Assert.Equal(Id(reply), Id(Assert.Single(p.GetProperty("replies").EnumerateArray())));
        }
        Assert.False(await _k.DbAsync(db => db.Set<Kb.Core.Content.Comment>().AnyAsync(x => x.Text == "Secret detail", Ct)));
        // A reply to a deleted comment is refused (only visible top-level comments take replies).
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader2.PostAsJsonAsync(Comments(), new { text = "more", parentId = Id(top) }, Ct)).StatusCode);
    }

    // comments.switch.site_off / space_off / page_off, comments.switch.no_override, comments.switch.own_delete_when_off
    [Fact]
    public async Task Off_AtAnyLevel_StopsPosting_HidesFromReaders_ButNotFromModerators_AndTheAuthorStillDeletes()
    {
        var mine = await PostAsync(_k.Reader, "Before");
        foreach (var off in new Func<Task>[]
                 {
                     () => _k.SiteAsync(comments: false, ratings: true),
                     () => _k.SpaceSwitchAsync(comments: false),
                     () => _k.PageSwitchAsync(_k.Page, comments: false),
                 })
        {
            await off();
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Reader.PostAsJsonAsync(Comments(), new { text = "During" }, Ct)).StatusCode);
            var reader = await ListAsync(_k.Reader2);
            Assert.False(reader.GetProperty("enabled").GetBoolean());
            Assert.False(reader.GetProperty("canComment").GetBoolean());
            Assert.Empty(Top(reader));
            Assert.Equal(0, reader.GetProperty("total").GetInt32());
            Assert.Single(Top(await ListAsync(_k.Supervisor)));
            await _k.SiteAsync(comments: true, ratings: true);
            await _k.SpaceSwitchAsync(comments: true);
            await _k.PageSwitchAsync(_k.Page, comments: true);
        }
        // Space off, page on: still off.
        await _k.SpaceSwitchAsync(comments: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Reader.PostAsJsonAsync(Comments(), new { text = "During" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Reader.DeleteAsync($"/api/v1/comments/{Id(mine)}", Ct)).StatusCode);
    }

    // comments.rate_limit
    [Fact]
    public async Task The11thCommentInAMinute_Is429_WithRetryAfter()
    {
        for (var i = 0; i < 10; i++) await PostAsync(_k.Reader, $"c{i}");
        var r = await _k.Reader.PostAsJsonAsync(Comments(), new { text = "eleven" }, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
        await PostAsync(_k.Reader2, "someone else"); // per person
        _clock.Advance(TimeSpan.FromSeconds(61));
        await PostAsync(_k.Reader, "later");
    }

    // comments.token.read_only
    [Fact]
    public async Task ATokenLists_ButCannotPost()
    {
        await PostAsync(_k.Reader, "Hello");
        var token = await _k.TokenAsync(_k.Reader);
        var list = await ListAsync(token);
        Assert.Single(Top(list));
        Assert.False(list.GetProperty("canComment").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync(Comments(), new { text = "From a script" }, Ct)).StatusCode); // Q-0039
        var bad = _k.F.NewClient();
        bad.DefaultRequestHeaders.Authorization = new("Bearer", "kbt_notatoken0000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync(Comments(), Ct)).StatusCode);
    }

    // comments.audit.no_text (create, edit, delete; hide and restore come with moderation)
    [Fact]
    public async Task ChangesAreAudited_WithoutTheText()
    {
        var c = await PostAsync(_k.Reader, "Marker-text-4711");
        (await _k.Reader.PutAsJsonAsync($"/api/v1/comments/{Id(c)}", new { text = "Marker-text-4712" }, Ct)).EnsureSuccessStatusCode();
        (await _k.Reader.DeleteAsync($"/api/v1/comments/{Id(c)}", Ct)).EnsureSuccessStatusCode();
        foreach (var action in new[] { AuditActions.CommentCreate, AuditActions.CommentEdit, AuditActions.CommentDelete })
        {
            var a = Assert.Single(await _k.AuditAsync(action));
            Assert.Equal(("comment", Id(c).ToString()), (a.TargetType, a.TargetId));
        }
        var all = await _k.DbAsync(db => db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct));
        Assert.DoesNotContain(all, d => d.Contains("Marker-text", StringComparison.Ordinal));
    }

    // comments.page.move, and a trashed page keeps its comments
    [Fact]
    public async Task Comments_FollowThePage_AcrossSpaces_AndThroughTheBin()
    {
        var c = await PostAsync(_k.Reader, "Travels along");
        var elsewhere = await _k.SpaceAsync("Elsewhere", "else");
        (await _k.Admin.PostAsJsonAsync($"/api/v1/pages/{_k.Page}/move", new { spaceId = elsewhere }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Reader.GetAsync(Comments(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Reader.DeleteAsync($"/api/v1/comments/{Id(c)}", Ct)).StatusCode);
        Assert.Equal(Id(c), Id(Assert.Single(Top(await ListAsync(_k.Admin)))));
        (await _k.Admin.PostAsJsonAsync($"/api/v1/pages/{_k.Page}/move", new { spaceId = _k.Space }, Ct)).EnsureSuccessStatusCode();

        (await _k.Admin.DeleteAsync($"/api/v1/pages/{_k.Page}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Reader.GetAsync(Comments(), Ct)).StatusCode);
        Assert.True(await _k.DbAsync(db => db.Set<Kb.Core.Content.Comment>().AnyAsync(x => x.Id == Id(c), Ct)));
    }

    [Fact]
    public async Task Paging_IsOverTopLevelComments_OldestFirst()
    {
        for (var i = 0; i < 3; i++) { await PostAsync(_k.Reader, $"n{i}"); _clock.Advance(TimeSpan.FromSeconds(30)); }
        var page = await (await _k.Reader2.GetAsync($"{Comments()}?limit=2&offset=1", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(["n1", "n2"], Top(page).Select(x => x.GetProperty("text").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader2.GetAsync($"{Comments()}?limit=201", Ct)).StatusCode);
    }
}
