namespace Kb.Web;

public sealed record CreateSpaceRequest(string Name, string? Key, string? Description, bool IsSensitive = false, bool IsPublic = false);
public sealed record UpdateSpaceRequest(string? Name, string? Description, bool? IsSensitive, bool? IsPublic,
    bool? CommentsEnabled = null, int? AgingDays = null, AgingAudience? AgingAudience = null, int? RecycleDays = null);
public sealed record GrantRoleRequest(PrincipalType PrincipalType, string PrincipalId, SpaceRole Role);

public static class SpaceRoutes
{
    public static void MapSpaceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/spaces", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListSpaces(), ct).ToHttp()).WithAction<ListSpaces>();

        api.MapGet("/spaces/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadSpace { SpaceId = id }, ct).ToHttp()).WithAction<ReadSpace>();

        api.MapPost("/spaces", (CreateSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateSpace { Name = req.Name, Key = req.Key, Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic }, ct)
                .ToHttp(space => Results.Created($"/api/v1/spaces/{space.Id}", space))).WithAction<CreateSpace>();

        api.MapPatch("/spaces/{id:guid}", (Guid id, UpdateSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateSpace { SpaceId = id, Name = req.Name, Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic,
                CommentsEnabled = req.CommentsEnabled, AgingDays = req.AgingDays, AgingAudience = req.AgingAudience, RecycleDays = req.RecycleDays }, ct).ToHttp()).WithAction<UpdateSpace>();

        api.MapGet("/spaces/{id:guid}/roles", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListSpaceRoles { SpaceId = id }, ct).ToHttp()).WithAction<ListSpaceRoles>();

        api.MapPut("/spaces/{id:guid}/roles", (Guid id, GrantRoleRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new GrantSpaceRole { SpaceId = id, PrincipalType = req.PrincipalType, PrincipalId = req.PrincipalId, Role = req.Role }, ct).ToHttp()).WithAction<GrantSpaceRole>();

        api.MapDelete("/spaces/{id:guid}/roles/{assignmentId:guid}", (Guid id, Guid assignmentId, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RevokeSpaceRole { SpaceId = id, AssignmentId = assignmentId }, ct).ToHttp()).WithAction<RevokeSpaceRole>();

        var admin = api.MapGroup("/admin");
        admin.MapGet("/spaces", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListAllSpaces(), ct).ToHttp()).WithAction<ListAllSpaces>();

        admin.MapPatch("/spaces/{id:guid}", (Guid id, UpdateSpaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateSpaceAdmin { SpaceId = id, Name = req.Name, Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic,
                CommentsEnabled = req.CommentsEnabled, AgingDays = req.AgingDays, AgingAudience = req.AgingAudience, RecycleDays = req.RecycleDays }, ct).ToHttp()).WithAction<UpdateSpaceAdmin>();
    }
}
