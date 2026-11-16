using Kb.Web.Actions;

namespace Kb.Web;

public sealed record PageFeedbackRequest(bool? CommentsEnabled = null, bool? RatingsEnabled = null);
public sealed record CommentRequest(string? Text, Guid? ParentId = null);
public sealed record CommentEditRequest(string? Text);

/// <summary>Page comments and ratings, and their switches (contracts/behaviour/comments.md, ratings.md).</summary>
public static class FeedbackRoutes
{
    public static void MapFeedbackEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/feedback", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadFeedbackSettings(), ct).ToHttp()).WithAction<ReadFeedbackSettings>();

        admin.MapPut("/feedback", (FeedbackSettingsDto req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveFeedbackSettings { Settings = req }, ct).ToHttp()).WithAction<SaveFeedbackSettings>();

        api.MapGet("/pages/{id:guid}/feedback", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadPageFeedback { PageId = id }, ct).ToHttp()).WithAction<ReadPageFeedback>();

        api.MapPut("/pages/{id:guid}/feedback", (Guid id, PageFeedbackRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SavePageFeedback { PageId = id, CommentsEnabled = req.CommentsEnabled, RatingsEnabled = req.RatingsEnabled }, ct).ToHttp()).WithAction<SavePageFeedback>();

        api.MapGet("/pages/{id:guid}/comments", (Guid id, int? limit, int? offset, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListComments { PageId = id, Limit = limit, Offset = offset }, ct).ToHttp()).WithAction<ListComments>();

        api.MapPost("/pages/{id:guid}/comments", (Guid id, CommentRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateComment { PageId = id, Text = req.Text, ParentId = req.ParentId }, ct)
                .ToHttp(c => Results.Created($"/api/v1/comments/{c.Id}", c))).WithAction<CreateComment>();

        api.MapPut("/comments/{id:guid}", (Guid id, CommentEditRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new EditComment { CommentId = id, Text = req.Text }, ct).ToHttp()).WithAction<EditComment>();

        api.MapDelete("/comments/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeleteComment { CommentId = id }, ct).ToHttp()).WithAction<DeleteComment>();

        api.MapPost("/comments/{id:guid}/hide", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new HideComment { CommentId = id }, ct).ToHttp()).WithAction<HideComment>();

        api.MapPost("/comments/{id:guid}/restore", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RestoreComment { CommentId = id }, ct).ToHttp()).WithAction<RestoreComment>();
    }
}
