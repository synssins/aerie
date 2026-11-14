using Kb.Web.Actions;
namespace Kb.Web;

public static class InventoryRoutes
{
    public static void MapInventoryEndpoints(this RouteGroupBuilder api)
    {
        var inv = api.MapGroup("/inventory");

        inv.MapGet("/me", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadInventoryMe(), ct).ToHttp()).WithAction<ReadInventoryMe>();

        inv.MapGet("/apps", (string? q, string? server, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListInvApps { Query = q, Server = server }, ct).ToHttp()).WithAction<ListInvApps>();

        inv.MapGet("/servers", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListInvServers(), ct).ToHttp()).WithAction<ListInvServers>();

        inv.MapGet("/apps/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadInvApp { ApplicationId = id }, ct).ToHttp()).WithAction<ReadInvApp>();

        inv.MapPost("/apps", (InvAppRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateInvApp { App = req }, ct).ToHttp(a => Results.Created($"/api/v1/inventory/apps/{a.Id}", a))).WithAction<CreateInvApp>();

        inv.MapPut("/apps/{id:guid}", (Guid id, InvAppRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateInvApp { ApplicationId = id, App = req }, ct).ToHttp()).WithAction<UpdateInvApp>();

        inv.MapDelete("/apps/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeleteInvApp { ApplicationId = id }, ct).ToHttp()).WithAction<DeleteInvApp>();

        inv.MapPut("/apps/{id:guid}/installs", (Guid id, InvInstallRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetInvInstall { ApplicationId = id, Server = req.Server ?? "", Version = req.Version ?? "" }, ct).ToHttp()).WithAction<SetInvInstall>();

        inv.MapDelete("/installs/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemoveInvInstall { InstallId = id }, ct).ToHttp()).WithAction<RemoveInvInstall>();

        inv.MapPost("/import", (InvImportRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ImportInventory { Csv = req.Csv ?? "", RemoveMissing = req.RemoveMissing, Apply = req.Apply }, ct).ToHttp()).WithAction<ImportInventory>();

        // What a CMDB or scan feed calls: a plugin sends the action itself, a script calls this with a write token.
        inv.MapPost("/sync", (InvSyncRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SyncInstalls { Source = req.Source ?? "", Rows = req.Rows ?? [], RemoveMissing = req.RemoveMissing, Apply = req.Apply ?? true }, ct).ToHttp())
            .WithAction<SyncInstalls>();

        var ai = api.MapGroup("/admin/ai");
        ai.MapGet("", (IActionBus bus, CancellationToken ct) => bus.SendAsync(new ReadAiSettings(), ct).ToHttp()).WithAction<ReadAiSettings>();
        ai.MapPut("", (AiSettingsRequest req, IActionBus bus, CancellationToken ct) => bus.SendAsync(new SaveAiSettings { Request = req }, ct).ToHttp()).WithAction<SaveAiSettings>();
        ai.MapPost("/test", (IActionBus bus, CancellationToken ct) => bus.SendAsync(new TestAi(), ct).ToHttp()).WithAction<TestAi>();
        ai.MapPost("/models", (AiModelsRequest req, IActionBus bus, CancellationToken ct) => bus.SendAsync(new ListAiModels { Request = req }, ct).ToHttp()).WithAction<ListAiModels>();

        var admin = api.MapGroup("/admin/inventory");
        admin.MapGet("/access", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadInventoryAccess(), ct).ToHttp()).WithAction<ReadInventoryAccess>();

        admin.MapPut("/access", (InvAccessDto req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveInventoryAccess { Access = req }, ct).ToHttp()).WithAction<SaveInventoryAccess>();

        api.MapPut("/pages/{id:guid}/details", (Guid id, PageDetailsRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SavePageDetails { PageId = id, Details = req }, ct).ToHttp()).WithAction<SavePageDetails>();
    }
}
