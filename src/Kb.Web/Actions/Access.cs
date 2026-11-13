using Kb.Core.Access;
using Kb.Core.Notifications;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// AccessService does the work, repeats the checks, writes the audit entry and notifies. These handlers add no audit entry of their own.
// AuthorizeAsync makes the service's 403 answer first, so that no plugin hook sees an action its caller may not do.

internal sealed class RequestAccessHandler(AccessService service) : IActionHandler<RequestAccess, AccessRequestedDto>
{
    // Anyone signed in may ask. The service answers 404 for a space that does not exist.
    public Task<Problem?> AuthorizeAsync(ActionContext<RequestAccess> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<AccessRequestedDto>> HandleAsync(ActionContext<RequestAccess> ctx)
    {
        try { return new AccessRequestedDto((await service.RequestAsync(ctx.Action.SpaceId, ctx.Actor.UserId!.Value, ctx.Action.Role, ctx.Action.Reason)).Id); }
        catch (NotFoundException) { return Problem.NotFound; }
    }
}

internal sealed class DecideAccessHandler(KbDbContext db, IAccess access, AccessService service) : IActionHandler<DecideAccess, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<DecideAccess> ctx)
    {
        var request = await db.AccessRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ctx.Action.RequestId, ctx.Cancel);
        // No role in the request's space: the request does not exist for this caller, an Admin in a sensitive space included.
        if (request is null || await access.RoleAsync(ctx.Actor, request.SpaceId, ctx.Cancel) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, request.SpaceId, SpaceAction.ApproveAccessRequests, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DecideAccess> ctx)
    {
        try { await service.DecideAsync(ctx.Action.RequestId, ctx.Actor.UserId!.Value, ctx.Action.Approve); return new Unit(); }
        catch (NotFoundException) { return Problem.NotFound; }
        catch (AccessDeniedException) { return Problem.Forbidden; }
        catch (InvalidOperationException e) { return Problem.Conflict(body: new { error = e.Message }); }
    }
}

internal sealed class OpenEmergencyAccessHandler(IAccess access, AccessService service) : IActionHandler<OpenEmergencyAccess, EmergencyAccessDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<OpenEmergencyAccess> ctx)
    {
        // The service answers a missing reason (400) before it looks at the caller. That answer stays first: HandleAsync gives it.
        if (!string.IsNullOrWhiteSpace(ctx.Action.Reason) && !await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<EmergencyAccessDto>> HandleAsync(ActionContext<OpenEmergencyAccess> ctx)
    {
        try { return new EmergencyAccessDto((await service.OpenEmergencyAsync(ctx.Action.SpaceId, ctx.Actor.UserId!.Value, ctx.Action.Reason)).ExpiresAt); }
        catch (NotFoundException) { return Problem.NotFound; }
        catch (AccessDeniedException) { return Problem.Forbidden; }
        catch (ArgumentException e) { return Problem.Invalid("reason", e.Message); }
        catch (InvalidOperationException e) { return Problem.Conflict(body: new { error = e.Message }); }
    }
}

// A person reaches only their own notifications: every query below is narrowed to the caller.

internal sealed class ListNotificationsHandler(KbDbContext db) : IActionHandler<ListNotifications, IReadOnlyList<NotificationDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListNotifications> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<IReadOnlyList<NotificationDto>>> HandleAsync(ActionContext<ListNotifications> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == me);
        if (ctx.Action.UnreadOnly) q = q.Where(n => n.ReadAt == null);
        var list = await q.ToListAsync(ctx.Cancel);
        // Sorted here, as before: SQLite cannot order by a DateTimeOffset.
        return Outcome.List<NotificationDto>([.. list.OrderByDescending(n => n.CreatedAt).Take(100)
            .Select(n => new NotificationDto(n.Id, n.UserId, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt))]);
    }
}

internal sealed class CountNotificationsHandler(KbDbContext db) : IActionHandler<CountNotifications, NotificationCountDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<CountNotifications> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<NotificationCountDto>> HandleAsync(ActionContext<CountNotifications> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        return new NotificationCountDto(await db.Notifications.CountAsync(n => n.UserId == me && n.ReadAt == null, ctx.Cancel));
    }
}

internal sealed class MarkNotificationHandler(KbDbContext db, TimeProvider time) : IActionHandler<MarkNotification, Unit>
{
    private Notification? _notification;

    public async Task<Problem?> AuthorizeAsync(ActionContext<MarkNotification> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        _notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == ctx.Action.NotificationId && n.UserId == me, ctx.Cancel);
        return _notification is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<MarkNotification> ctx)
    {
        _notification!.ReadAt ??= time.GetUtcNow();
        await db.SaveChangesAsync();
        return new Unit();
    }
}

internal sealed class MarkAllNotificationsHandler(KbDbContext db, TimeProvider time) : IActionHandler<MarkAllNotifications, Unit>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<MarkAllNotifications> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<MarkAllNotifications> ctx)
    {
        var (me, now) = (ctx.Actor.UserId!.Value, time.GetUtcNow());
        foreach (var n in await db.Notifications.Where(n => n.UserId == me && n.ReadAt == null).ToListAsync(ctx.Cancel)) n.ReadAt = now;
        await db.SaveChangesAsync();
        return new Unit();
    }
}
