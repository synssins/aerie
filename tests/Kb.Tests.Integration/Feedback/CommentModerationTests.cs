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

/// <summary>Slice C2: moderation by the space's Supervisors and Owners (contracts/behaviour/comments.md, Moderation; question Q-0024).</summary>
public sealed class CommentModerationTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private FeedbackKit _k = null!;

    public async ValueTask InitializeAsync() =>
        _k = await FeedbackKit.CreateAsync(new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock))));
    public async ValueTask DisposeAsync() => await _k.DisposeAsync();

    private string Comments => $"/api/v1/pages/{_k.Page}/comments";

    private async Task<Guid> PostAsync(HttpClient c, string text, Guid? parent = null) =>
        (await (await c.PostAsJsonAsync(Comments, new { text, parentId = parent }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private async Task<List<JsonElement>> TopAsync(HttpClient c) =>
        [.. (await (await c.GetAsync(Comments, Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("comments").EnumerateArray()];

    private static JsonElement? Find(List<JsonElement> top, Guid id) =>
        top.Concat(top.SelectMany(t => t.GetProperty("replies").EnumerateArray())).Cast<JsonElement?>().FirstOrDefault(c => c!.Value.GetProperty("id").GetGuid() == id);

    private static Task<HttpResponseMessage> Hide(HttpClient c, Guid id) => c.PostAsync($"/api/v1/comments/{id}/hide", null, Ct);
    private static Task<HttpResponseMessage> Restore(HttpClient c, Guid id) => c.PostAsync($"/api/v1/comments/{id}/restore", null, Ct);
    private static Task<HttpResponseMessage> Delete(HttpClient c, Guid id) => c.DeleteAsync($"/api/v1/comments/{id}", Ct);

    // comments.moderate.reader_refused, comments.moderate.admin_without_role
    [Fact]
    public async Task OnlySupervisorsAndOwners_Moderate_NotAnAdminWithoutARole()
    {
        var id = await PostAsync(_k.Reader, "Rude");
        foreach (var c in new[] { _k.Reader2, _k.Contributor, _k.Admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Hide(c, id)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Restore(c, id)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Delete(c, id)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Hide(_k.Outsider, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hide(_k.F.NewClient(), id)).StatusCode);
        Assert.Equal("visible", Find(await TopAsync(_k.Reader2), id)!.Value.GetProperty("state").GetString());
    }

    // comments.hide.restore
    [Fact]
    public async Task ASupervisorHides_TheAuthorStillSeesIt_OthersDoNot_AndRestoreBringsItBack()
    {
        var id = await PostAsync(_k.Reader, "Off topic");
        var reply = await PostAsync(_k.Reader2, "Indeed", id);
        Assert.Equal(HttpStatusCode.NoContent, (await Hide(_k.Supervisor, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Hide(_k.Supervisor, id)).StatusCode); // again: no change
        Assert.Single(await _k.AuditAsync(AuditActions.CommentHide));

        // The other Reader sees a placeholder, because the comment has a reply they see.
        var other = Find(await TopAsync(_k.Reader2), id)!.Value;
        Assert.Equal("hidden", other.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("text").ValueKind);
        Assert.NotNull(Find(await TopAsync(_k.Reader2), reply));
        var author = Find(await TopAsync(_k.Reader), id)!.Value;
        Assert.Equal(("hidden", "Off topic"), (author.GetProperty("state").GetString(), author.GetProperty("text").GetString()));
        var mod = Find(await TopAsync(_k.Owner), id)!.Value;
        Assert.Equal(("hidden", "Off topic"), (mod.GetProperty("state").GetString(), mod.GetProperty("text").GetString()));
        Assert.True(mod.GetProperty("canModerate").GetBoolean());
        // A hidden comment takes no edits and no replies.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Reader.PutAsJsonAsync($"/api/v1/comments/{id}", new { text = "Edit" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Reader2.PostAsJsonAsync(Comments, new { text = "more", parentId = id }, Ct)).StatusCode);

        // Without a reply the hidden comment is simply not there for others.
        var lone = await PostAsync(_k.Reader, "Lone");
        Assert.Equal(HttpStatusCode.NoContent, (await Hide(_k.Owner, lone)).StatusCode);
        Assert.Null(Find(await TopAsync(_k.Reader2), lone));

        Assert.Equal(HttpStatusCode.NoContent, (await Restore(_k.Supervisor, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Restore(_k.Supervisor, id)).StatusCode); // already visible
        Assert.Equal(("visible", "Off topic"), (Find(await TopAsync(_k.Reader2), id)!.Value.GetProperty("state").GetString(), Find(await TopAsync(_k.Reader2), id)!.Value.GetProperty("text").GetString()));
        Assert.Single(await _k.AuditAsync(AuditActions.CommentRestore));
    }

    // comments.moderator_delete.restore
    [Fact]
    public async Task AnOwnerDeletes_OnlyModeratorsSeeIt_AndRestoreBringsItBack()
    {
        var id = await PostAsync(_k.Reader, "Wrong");
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Owner, id)).StatusCode); // again: no change
        var audit = Assert.Single(await _k.AuditAsync(AuditActions.CommentDelete));
        Assert.Contains("moderator", audit.DataJson, StringComparison.Ordinal);
        Assert.Null(Find(await TopAsync(_k.Reader), id)); // not even its author
        Assert.Null(Find(await TopAsync(_k.Reader2), id));
        var mod = Find(await TopAsync(_k.Supervisor), id)!.Value;
        Assert.Equal(("deleted", "Wrong"), (mod.GetProperty("state").GetString(), mod.GetProperty("text").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await Restore(_k.Supervisor, id)).StatusCode);
        Assert.Equal("visible", Find(await TopAsync(_k.Reader2), id)!.Value.GetProperty("state").GetString());
    }

    // comments.moderator_delete.purge
    [Fact]
    public async Task AModeratorDelete_IsPurged30DaysLater()
    {
        var id = await PostAsync(_k.Reader, "Gone soon");
        var reply = await PostAsync(_k.Reader2, "Reply stays", id);
        var lone = await PostAsync(_k.Reader, "Gone entirely");
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Owner, lone)).StatusCode);
        _clock.Advance(TimeSpan.FromDays(29));
        // The clock moves past a session's life: sign in again each time.
        Assert.NotNull(Find(await TopAsync(await _k.SignInAsync("sam")), lone));
        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var supervisor = await _k.SignInAsync("sam");
        Assert.Null(Find(await TopAsync(supervisor), lone));
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(supervisor, lone)).StatusCode);
        // With a reply, a placeholder stays without its text.
        var placeholder = Find(await TopAsync(supervisor), id)!.Value;
        Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("text").ValueKind);
        Assert.NotNull(Find(await TopAsync(supervisor), reply));
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(supervisor, id)).StatusCode);

        await Kb.Web.Actions.CommentPurge.RunAsync(_k.F.Services, _clock.GetUtcNow(), Ct);
        Assert.False(await _k.DbAsync(db => db.Comments.AnyAsync(c => c.Id == lone, Ct)));
        Assert.False(await _k.DbAsync(db => db.Comments.AnyAsync(c => c.Text == "Gone soon", Ct)));
    }

    // comments.delete.own: an author's delete cannot be restored, not even by moderators.
    [Fact]
    public async Task AnAuthorsDelete_CannotBeRestored_AndAModeratorsOwnDelete_IsAnAuthorsDelete()
    {
        var id = await PostAsync(_k.Reader, "Mine");
        await PostAsync(_k.Reader2, "Reply", id);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Reader, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(_k.Supervisor, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Hide(_k.Supervisor, id)).StatusCode);

        var own = await PostAsync(_k.Supervisor, "My own words");
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Supervisor, own)).StatusCode);
        Assert.Null(Find(await TopAsync(_k.Owner), own));
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(_k.Owner, own)).StatusCode);
    }

    // Moderation goes on while comments are off.
    [Fact]
    public async Task Moderators_StillModerate_WhileCommentsAreOff()
    {
        var id = await PostAsync(_k.Reader, "Before");
        await _k.PageSwitchAsync(_k.Page, comments: false);
        Assert.Equal(HttpStatusCode.NoContent, (await Hide(_k.Supervisor, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Restore(_k.Supervisor, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(_k.Supervisor, id)).StatusCode);
    }

    // comments.audit.no_text, the moderation part.
    [Fact]
    public async Task Moderation_IsAudited_WithoutTheText()
    {
        var id = await PostAsync(_k.Reader, "Marker-text-0815");
        (await Hide(_k.Supervisor, id)).EnsureSuccessStatusCode();
        (await Restore(_k.Supervisor, id)).EnsureSuccessStatusCode();
        (await Delete(_k.Supervisor, id)).EnsureSuccessStatusCode();
        foreach (var action in new[] { AuditActions.CommentHide, AuditActions.CommentRestore, AuditActions.CommentDelete })
            Assert.Equal(("comment", id.ToString()), Assert.Single(await _k.AuditAsync(action)) is var a ? (a.TargetType, a.TargetId) : default);
        Assert.DoesNotContain(await _k.DbAsync(db => db.AuditEntries.Select(a => a.DataJson).ToListAsync(Ct)), d => d.Contains("Marker-text", StringComparison.Ordinal));
    }
}
