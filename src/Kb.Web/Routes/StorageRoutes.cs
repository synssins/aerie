using Kb.Web.Actions;

namespace Kb.Web;

public sealed record StorageMoveRequest(Guid ToId);
public sealed record StoragePreviewRequest(bool On);
public sealed record StoragePlaceRequest(string? Name, string? Kind, Dictionary<string, string?>? Settings, Dictionary<string, string?>? Secrets);

/// <summary>Administration > Storage. Admins only; every change is audited; no answer carries a secret.</summary>
public static class StorageRoutes
{
    public static void MapStorageEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/admin").MapGroup("/storage"); // no filter: each handler answers 403 to a caller who is not an Admin

        g.MapGet("/", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadStorage(), ct).ToHttp()).WithAction<ReadStorage>();

        g.MapPost("/places", (StoragePlaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AddStoragePlace { Name = req.Name, Kind = req.Kind, Settings = req.Settings, Secrets = req.Secrets }, ct)
                .ToHttp(place => Results.Created($"/api/v1/admin/storage/places/{place.Id}", place))).WithAction<AddStoragePlace>();

        g.MapPut("/places/{id:guid}", (Guid id, StoragePlaceRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveStoragePlace { Id = id, Name = req.Name, Settings = req.Settings, Secrets = req.Secrets }, ct).ToHttp()).WithAction<SaveStoragePlace>();

        g.MapPost("/places/{id:guid}/test", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new TestStoragePlace { Id = id }, ct).ToHttp()).WithAction<TestStoragePlace>();

        g.MapDelete("/places/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemoveStoragePlace { Id = id }, ct).ToHttp()).WithAction<RemoveStoragePlace>();

        g.MapPost("/move", (StorageMoveRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new StartStorageMove { ToId = req.ToId }, ct).ToHttp(move => Results.Created("/api/v1/admin/storage", move))).WithAction<StartStorageMove>();

        g.MapPost("/move/copy-again", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CopyStorageMoveAgain(), ct).ToHttp()).WithAction<CopyStorageMoveAgain>();

        g.MapPut("/move/preview", (StoragePreviewRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new PreviewStorageMove { On = req.On }, ct).ToHttp()).WithAction<PreviewStorageMove>();

        g.MapPost("/move/switch", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SwitchStorageMove(), ct).ToHttp()).WithAction<SwitchStorageMove>();

        g.MapPost("/move/cancel", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CancelStorageMove(), ct).ToHttp()).WithAction<CancelStorageMove>();
    }

    /// <summary>The first line of a system's error. The file stores, the mover and log shipping use it too.</summary>
    internal static string FirstLine(string message) => message.Split('\r', '\n')[0];
}
