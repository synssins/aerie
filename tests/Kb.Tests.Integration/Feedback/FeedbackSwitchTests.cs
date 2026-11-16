using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Feedback;

/// <summary>Slice F: the comment and rating switches at site, space and page level (contracts/behaviour/comments.md, Switching comments on and off; Q-0025).</summary>
public sealed class FeedbackSwitchTests : IAsyncLifetime
{
    private FeedbackKit _k = null!;

    public async ValueTask InitializeAsync() => _k = await FeedbackKit.CreateAsync();
    public async ValueTask DisposeAsync() => await _k.DisposeAsync();

    private static async Task<JsonElement> LevelsAsync(HttpClient c, Guid page) => await (await c.GetAsync($"/api/v1/pages/{page}/feedback", Ct)).JsonAsync(HttpStatusCode.OK);

    private static (bool Site, bool Space, bool Page, bool Effective) Of(JsonElement e) =>
        (e.GetProperty("site").GetBoolean(), e.GetProperty("space").GetBoolean(), e.GetProperty("page").GetBoolean(), e.GetProperty("effective").GetBoolean());

    [Fact]
    public async Task SiteSettings_AreAdminOnly_DefaultOn_AndAudited()
    {
        var s = await (await _k.Admin.GetAsync("/api/v1/admin/feedback", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.True(s.GetProperty("commentsEnabled").GetBoolean());
        Assert.True(s.GetProperty("ratingsEnabled").GetBoolean());
        foreach (var c in new[] { _k.Owner, _k.Reader })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/admin/feedback", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/v1/admin/feedback", new { commentsEnabled = false, ratingsEnabled = false }, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.F.NewClient().GetAsync("/api/v1/admin/feedback", Ct)).StatusCode);

        var saved = await (await _k.Admin.PutAsJsonAsync("/api/v1/admin/feedback", new { commentsEnabled = false, ratingsEnabled = true }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.False(saved.GetProperty("commentsEnabled").GetBoolean());
        var read = await (await _k.Admin.GetAsync("/api/v1/admin/feedback", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.False(read.GetProperty("commentsEnabled").GetBoolean());
        Assert.True(read.GetProperty("ratingsEnabled").GetBoolean());
        Assert.Single(await _k.AuditAsync(AuditActions.FeedbackSettingsSave));
    }

    [Fact]
    public async Task Space_HasRatingsEnabled_NextToCommentsEnabled_ChangedByWhoChangesSettings()
    {
        var space = await (await _k.Owner.GetAsync($"/api/v1/spaces/{_k.Space}", Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.True(space.GetProperty("commentsEnabled").GetBoolean());
        Assert.True(space.GetProperty("ratingsEnabled").GetBoolean());
        var changed = await (await _k.Owner.PatchAsJsonAsync($"/api/v1/spaces/{_k.Space}", new { ratingsEnabled = false }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.False(changed.GetProperty("ratingsEnabled").GetBoolean());
        Assert.True(changed.GetProperty("commentsEnabled").GetBoolean());
        // A Supervisor does not change space settings.
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Supervisor.PatchAsJsonAsync($"/api/v1/spaces/{_k.Space}", new { ratingsEnabled = true }, Ct)).StatusCode);
        // The Admin's own route too.
        var admin = await (await _k.Admin.PatchAsJsonAsync($"/api/v1/admin/spaces/{_k.Space}", new { ratingsEnabled = true }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.True(admin.GetProperty("ratingsEnabled").GetBoolean());
    }

    [Fact]
    public async Task PageFeedback_DefaultsOn_AndShowsEachLevel()
    {
        var f = await LevelsAsync(_k.Reader, _k.Page);
        Assert.Equal((true, true, true, true), Of(f.GetProperty("comments")));
        Assert.Equal((true, true, true, true), Of(f.GetProperty("ratings")));
    }

    // comments.switch.site_off / space_off / page_off and the ratings ones, as far as the settings answers go (slice F).
    [Fact]
    public async Task EachLevel_TurnsItOff_AndNoLevelOverridesOneAbove()
    {
        await _k.SiteAsync(comments: false, ratings: true);
        var f = await LevelsAsync(_k.Reader, _k.Page);
        Assert.Equal((false, true, true, false), Of(f.GetProperty("comments")));
        Assert.Equal((true, true, true, true), Of(f.GetProperty("ratings")));

        await _k.SiteAsync(comments: true, ratings: true);
        await _k.SpaceSwitchAsync(comments: false);
        // comments.switch.no_override: space off, page on.
        await _k.PageSwitchAsync(_k.Page, comments: true);
        Assert.Equal((true, false, true, false), Of((await LevelsAsync(_k.Reader, _k.Page)).GetProperty("comments")));

        await _k.SpaceSwitchAsync(comments: true);
        var saved = await (await _k.Supervisor.PutAsJsonAsync($"/api/v1/pages/{_k.Page}/feedback", new { ratingsEnabled = false }, Ct)).JsonAsync(HttpStatusCode.OK);
        Assert.Equal((true, true, false, false), Of(saved.GetProperty("ratings")));
        Assert.Equal((true, true, true, true), Of(saved.GetProperty("comments"))); // null leaves it as it is

        // ratings.switch.no_override: site off, space and page on.
        await _k.PageSwitchAsync(_k.Page, ratings: true);
        await _k.SiteAsync(comments: true, ratings: false);
        Assert.Equal((false, true, true, false), Of((await LevelsAsync(_k.Reader, _k.Page)).GetProperty("ratings")));
    }

    // comments.switch.page_by_supervisor: Supervisors and Owners set the page switch; nobody else, a site administrator without a role included (Q-0024).
    [Fact]
    public async Task PageSwitch_IsForSupervisorsAndOwners_AndAudited()
    {
        foreach (var c in new[] { _k.Reader, _k.Contributor, _k.Admin })
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"/api/v1/pages/{_k.Page}/feedback", new { commentsEnabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.PutAsJsonAsync($"/api/v1/pages/{_k.Page}/feedback", new { commentsEnabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.Supervisor.PutAsJsonAsync($"/api/v1/pages/{_k.Page}/feedback", new { commentsEnabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.Owner.PutAsJsonAsync($"/api/v1/pages/{_k.Page}/feedback", new { commentsEnabled = true }, Ct)).StatusCode);
        var audit = await _k.AuditAsync(AuditActions.PageFeedbackSave);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Equal(("page", _k.Page.ToString()), (a.TargetType, a.TargetId)));
    }

    [Fact]
    public async Task PageFeedback_IsHiddenFromThoseWhoCannotReadThePage_AndClosedToGuestsAndTokens()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Outsider.GetAsync($"/api/v1/pages/{_k.Page}/feedback", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Reader.GetAsync($"/api/v1/pages/{Guid.NewGuid()}/feedback", Ct)).StatusCode);
        // A page in a space anyone may read: a guest still gets 401.
        var open = await _k.SpaceAsync("Open", "open", "Anyone");
        var page = await _k.PageAsync(open, "Public page");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.F.NewClient().GetAsync($"/api/v1/pages/{page}/feedback", Ct)).StatusCode);
        var token = await _k.TokenAsync(_k.Reader);
        // A valid token on a session-only call gets 403 (comments.md; question Q-0039).
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync($"/api/v1/pages/{_k.Page}/feedback", Ct)).StatusCode);
    }
}
