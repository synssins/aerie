namespace Kb.Web;

public sealed record MoveRequest(Guid? SpaceId, Guid? ParentId);

public static class PageStructureRoutes
{
    public static void MapPageStructureEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/pages/{id:guid}/move", (Guid id, MoveRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new MovePage { PageId = id, SpaceId = req.SpaceId, ParentId = req.ParentId }, ct).ToHttp()).WithAction<MovePage>();

        // Delete = move to the recycle bin. Nothing here destroys data.
        api.MapDelete("/pages/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeletePage { PageId = id }, ct).ToHttp()).WithAction<DeletePage>();

        api.MapGet("/spaces/{id:guid}/bin", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListBin { SpaceId = id }, ct).ToHttp()).WithAction<ListBin>();

        api.MapPost("/spaces/{id:guid}/bin/empty", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new EmptyBin { SpaceId = id }, ct).ToHttp()).WithAction<EmptyBin>();

        api.MapPost("/pages/{id:guid}/restore", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RestorePage { PageId = id }, ct).ToHttp()).WithAction<RestorePage>();

        api.MapGroup("/admin").MapGet("/bin", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListAllBins(), ct).ToHttp()).WithAction<ListAllBins>();

        // The bin, browsed like the knowledge base: a tree, and an item of it opened to read.
        api.MapGet("/spaces/{id:guid}/bin/tree", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new BrowseBin { SpaceId = id }, ct).ToHttp()).WithAction<BrowseBin>();

        api.MapGroup("/admin").MapGet("/bin/tree", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new BrowseAllBins(), ct).ToHttp()).WithAction<BrowseAllBins>();

        api.MapGet("/bin/pages/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadBinPage { PageId = id }, ct).ToHttp()).WithAction<ReadBinPage>();
    }
}
