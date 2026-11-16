using Kb.Web.Actions;

namespace Kb.Web;

public sealed record PageFeedbackRequest(bool? CommentsEnabled = null, bool? RatingsEnabled = null);

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
    }
}
