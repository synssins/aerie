using System.Net;
using System.Net.Http.Json;
using Kb.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Feedback;

/// <summary>Slice C3: comment notifications (contracts/behaviour/comments.md, Notifications; question Q-0026).</summary>
public sealed class CommentNotificationTests : IAsyncLifetime
{
    private FeedbackKit _k = null!;

    public async ValueTask InitializeAsync()
    {
        _k = await FeedbackKit.CreateAsync();
        // The page's owner (details.owner) is the Owner of the space.
        await _k.F.WithDbAsync(async db =>
        {
            var page = await db.Pages.FirstAsync(p => p.Id == _k.Page, Ct);
            (page.OwnerType, page.OwnerId) = (Kb.Plugins.PrincipalType.User, _k.Ids["olga"].ToString());
            await db.SaveChangesAsync(Ct);
        });
    }

    public async ValueTask DisposeAsync() => await _k.DisposeAsync();

    private async Task<Guid> PostAsync(HttpClient c, string text, Guid? parent = null) =>
        (await (await c.PostAsJsonAsync($"/api/v1/pages/{_k.Page}/comments", new { text, parentId = parent }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private Task<List<Notification>> OfAsync(string user, string kind) => _k.DbAsync(db => db.Notifications.Where(n => n.UserId == _k.Ids[user] && n.Kind == kind).ToListAsync(Ct));

    // comments.notify.owner_and_reply
    [Fact]
    public async Task AComment_NotifiesThePageOwner_AndAReply_TheCommentsAuthor_NeverTheWriter()
    {
        var top = await PostAsync(_k.Reader, "Secret-text-31");
        var n = Assert.Single(await OfAsync("olga", NotificationKinds.CommentPage));
        Assert.Equal($"/p/{_k.Page}", n.Link);
        Assert.DoesNotContain("Secret-text", n.Title + n.Body, StringComparison.Ordinal);
        Assert.Empty(await OfAsync("rita", NotificationKinds.CommentPage));

        await PostAsync(_k.Reader2, "Reply-text-32", top);
        var r = Assert.Single(await OfAsync("rita", NotificationKinds.CommentReply));
        Assert.Equal($"/p/{_k.Page}", r.Link);
        Assert.Empty(await OfAsync("rory", NotificationKinds.CommentReply));
        Assert.Single(await OfAsync("olga", NotificationKinds.CommentPage)); // a reply is not a new top-level comment

        // Nobody is notified of their own comment or reply.
        var own = await PostAsync(_k.Owner, "On my own page");
        await PostAsync(_k.Owner, "And my own reply", own);
        Assert.Single(await OfAsync("olga", NotificationKinds.CommentPage));
        Assert.Empty(await OfAsync("olga", NotificationKinds.CommentReply));
    }

    [Fact]
    public async Task NobodyIsNotified_AboutAPageTheyCannotRead_OrForAGroupOwner()
    {
        await _k.F.WithDbAsync(async db =>
        {
            var page = await db.Pages.FirstAsync(p => p.Id == _k.Page, Ct);
            page.OwnerId = _k.Ids["otto"].ToString(); // no role in the space
            await db.SaveChangesAsync(Ct);
        });
        await PostAsync(_k.Reader, "Hello");
        Assert.Empty(await OfAsync("otto", NotificationKinds.CommentPage));

        await _k.F.WithDbAsync(async db =>
        {
            var page = await db.Pages.FirstAsync(p => p.Id == _k.Page, Ct);
            (page.OwnerType, page.OwnerId) = (Kb.Plugins.PrincipalType.Group, "erp-support");
            await db.SaveChangesAsync(Ct);
        });
        await PostAsync(_k.Reader, "Hello again");
        Assert.Equal(0, await _k.DbAsync(db => db.Notifications.CountAsync(x => x.Kind == NotificationKinds.CommentPage, Ct)));
    }

    // The usual per-person preference: in-app on by default, and it can be turned off.
    [Fact]
    public async Task ThePreference_TurnsItOff()
    {
        var settings = await (await _k.Owner.GetAsync("/api/v1/me/settings", Ct)).JsonAsync(HttpStatusCode.OK);
        var body = System.Text.Json.Nodes.JsonNode.Parse(settings.GetRawText())!.AsObject();
        body["notify"] = new System.Text.Json.Nodes.JsonObject { ["comment.page"] = new System.Text.Json.Nodes.JsonObject { ["inApp"] = false, ["email"] = false } };
        (await _k.Owner.PutAsJsonAsync("/api/v1/me/settings", body, Ct)).EnsureSuccessStatusCode();
        await PostAsync(_k.Reader, "Quiet please");
        Assert.Empty(await OfAsync("olga", NotificationKinds.CommentPage));
    }
}
