using Kb.Web.Actions;
namespace Kb.Web;

public sealed record CreateSpaceRequest(string Name, string? Key, string? Description, bool IsSensitive = false, SpaceVisibility Visibility = SpaceVisibility.Private);
public sealed record UpdateSpaceRequest(string? Name, string? Description, bool? IsSensitive, SpaceVisibility? Visibility,
    bool? CommentsEnabled = null, int? AgingDays = null, AgingAudience? AgingAudience = null, int? RecycleDays = null);
public sealed record GrantRoleRequest(PrincipalType PrincipalType, string PrincipalId, SpaceRole? Role = null, Guid? RoleId = null);
public sealed record DeleteSpaceRequest(string? Confirm);

public static class SpaceRoutes
{
    public static void MapSpaceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/spaces", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListSpaces(), ct).ToHttp()).AllowAnonymous().WithAction<ListSpaces>();

        api.MapGet("/spaces/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadSpace { SpaceId = id }, ct).ToHttp()).AllowAnonymous().WithAction<ReadSpace>();

        api.MapPost("/spaces", (CreateSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateSpace { Name = req.Name, Key = req.Key, Description = req.Description, IsSensitive = req.IsSensitive, Visibility = req.Visibility }, ct)
                .ToHttp(space => Results.Created($"/api/v1/spaces/{space.Id}", space))).WithAction<CreateSpace>();

        api.MapPatch("/spaces/{id:guid}", (Guid id, UpdateSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateSpace { SpaceId = id, Name = req.Name, Description = req.Description, IsSensitive = req.IsSensitive, Visibility = req.Visibility,
                CommentsEnabled = req.CommentsEnabled, AgingDays = req.AgingDays, AgingAudience = req.AgingAudience, RecycleDays = req.RecycleDays }, ct).ToHttp()).WithAction<UpdateSpace>();

        api.MapGet("/spaces/{id:guid}/roles", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListSpaceRoles { SpaceId = id }, ct).ToHttp()).WithAction<ListSpaceRoles>();

        api.MapPut("/spaces/{id:guid}/roles", (Guid id, GrantRoleRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new GrantSpaceRole { SpaceId = id, PrincipalType = req.PrincipalType, PrincipalId = req.PrincipalId, Role = req.Role, RoleId = req.RoleId }, ct).ToHttp()).WithAction<GrantSpaceRole>();

        api.MapDelete("/spaces/{id:guid}/roles/{assignmentId:guid}", (Guid id, Guid assignmentId, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RevokeSpaceRole { SpaceId = id, AssignmentId = assignmentId }, ct).ToHttp()).WithAction<RevokeSpaceRole>();

        var admin = api.MapGroup("/admin");
        api.MapGet("/roles", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListRoles(), ct).ToHttp()).WithAction<ListRoles>();

        admin.MapPost("/roles", (RoleRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateRole { Name = req.Name ?? "", Permissions = req.Permissions ?? [] }, ct).ToHttp(r => Results.Created($"/api/v1/roles/{r.Id}", r))).WithAction<CreateRole>();

        admin.MapPut("/roles/{id:guid}", (Guid id, RoleRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateRole { RoleId = id, Name = req.Name ?? "", Permissions = req.Permissions ?? [] }, ct).ToHttp()).WithAction<UpdateRole>();

        admin.MapDelete("/roles/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeleteRole { RoleId = id }, ct).ToHttp()).WithAction<DeleteRole>();

        admin.MapGet("/spaces", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListAllSpaces(), ct).ToHttp()).WithAction<ListAllSpaces>();

        admin.MapGet("/spaces/deleted", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListDeletedSpaces(), ct).ToHttp()).WithAction<ListDeletedSpaces>();

        // A POST with the phrase in its body, not a DELETE: the phrase is part of the request.
        admin.MapPost("/spaces/{id:guid}/delete", (Guid id, DeleteSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeleteSpace { SpaceId = id, Confirm = req.Confirm }, ct).ToHttp()).WithAction<DeleteSpace>();

        admin.MapPost("/spaces/{id:guid}/restore", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RestoreSpace { SpaceId = id }, ct).ToHttp()).WithAction<RestoreSpace>();

        admin.MapPatch("/spaces/{id:guid}", (Guid id, UpdateSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateSpaceAdmin { SpaceId = id, Name = req.Name, Description = req.Description, IsSensitive = req.IsSensitive, Visibility = req.Visibility,
                CommentsEnabled = req.CommentsEnabled, AgingDays = req.AgingDays, AgingAudience = req.AgingAudience, RecycleDays = req.RecycleDays }, ct).ToHttp()).WithAction<UpdateSpaceAdmin>();
    }
}
