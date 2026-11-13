namespace Kb.Web;

public sealed record AccessRequestBody(SpaceRole Role, string? Reason);
public sealed record DecideBody(bool Approve);
public sealed record EmergencyBody(string Reason);

public static class AccessRoutes
{
    public static void MapAccessEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/spaces/{id:guid}/access-requests", (Guid id, AccessRequestBody body, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RequestAccess { SpaceId = id, Role = body.Role, Reason = body.Reason }, ct)
                .ToHttp(made => Results.Accepted(value: made))).WithAction<RequestAccess>();

        api.MapPost("/access-requests/{id:guid}/decision", (Guid id, DecideBody body, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DecideAccess { RequestId = id, Approve = body.Approve }, ct).ToHttp()).WithAction<DecideAccess>();

        api.MapPost("/spaces/{id:guid}/emergency-access", (Guid id, EmergencyBody body, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new OpenEmergencyAccess { SpaceId = id, Reason = body.Reason }, ct).ToHttp()).WithAction<OpenEmergencyAccess>();
    }

    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications", (bool? unread, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListNotifications { UnreadOnly = unread == true }, ct).ToHttp()).WithAction<ListNotifications>();

        api.MapGet("/notifications/count", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CountNotifications(), ct).ToHttp()).WithAction<CountNotifications>();

        api.MapPost("/notifications/{id:guid}/read", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new MarkNotification { NotificationId = id }, ct).ToHttp()).WithAction<MarkNotification>();

        api.MapPost("/notifications/read-all", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new MarkAllNotifications(), ct).ToHttp()).WithAction<MarkAllNotifications>();
    }
}
