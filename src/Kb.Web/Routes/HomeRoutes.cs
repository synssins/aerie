namespace Kb.Web;

public sealed record PostAnnouncementRequest(string Title, string? Body, DateTimeOffset? ExpiresAt);
public sealed record AnnouncerRequest(bool Enabled);

public static class HomeRoutes
{
    public static void MapHomeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/home", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadHome(), ct).ToHttp()).WithAction<ReadHome>();

        api.MapPost("/announcements", (PostAnnouncementRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new PostAnnouncement { Title = req.Title, Body = req.Body, ExpiresAt = req.ExpiresAt }, ct)
                .ToHttp(made => Results.Created($"/api/v1/announcements/{made.Id}", made))).WithAction<PostAnnouncement>();

        api.MapDelete("/announcements/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemoveAnnouncement { AnnouncementId = id }, ct).ToHttp()).WithAction<RemoveAnnouncement>();

        // Who may post announcements. No filter on the group: the handler answers 403 to a caller who is not an Admin.
        api.MapGroup("/admin").MapPut("/users/{id:guid}/announcer", (Guid id, AnnouncerRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetAnnouncer { UserId = id, Enabled = req.Enabled }, ct).ToHttp()).WithAction<SetAnnouncer>();
    }
}
