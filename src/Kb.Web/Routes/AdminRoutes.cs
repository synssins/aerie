using Kb.Web.Actions;

namespace Kb.Web;

public sealed record InviteRequest(string UserName, string DisplayName, string? Email, bool IsAdmin = false);
public sealed record UserPermissionRequest(string Permission, bool Granted);

public static class AdminRoutes
{
    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/audit/verify", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new VerifyAudit(), ct).ToHttp()).WithAction<VerifyAudit>();

        admin.MapGet("/audit", (int? take, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListAudit { Take = take }, ct).ToHttp()).WithAction<ListAudit>();

        admin.MapGet("/users", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListUsers(), ct).ToHttp()).WithAction<ListUsers>();

        admin.MapPut("/users/{id:guid}/permissions", (Guid id, UserPermissionRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetUserPermission { UserId = id, Permission = req.Permission, Granted = req.Granted }, ct).ToHttp()).WithAction<SetUserPermission>();

        admin.MapPost("/users/invite", (InviteRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new InviteUser { UserName = req.UserName, DisplayName = req.DisplayName, Email = req.Email, IsAdmin = req.IsAdmin }, ct).ToHttp()).WithAction<InviteUser>();

        admin.MapPost("/users/{id:guid}/password-link", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new NewPasswordLink { UserId = id }, ct).ToHttp()).WithAction<NewPasswordLink>();

        admin.MapDelete("/users/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeleteUser { UserId = id }, ct).ToHttp()).WithAction<DeleteUser>();

        admin.MapPost("/auth/local/disable", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DisableLocalSignIn(), ct).ToHttp()).WithAction<DisableLocalSignIn>();

        admin.MapPut("/settings/public-space", (bool enabled, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetPublicSpace { Enabled = enabled }, ct).ToHttp()).WithAction<SetPublicSpace>();
    }
}
