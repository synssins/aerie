namespace Kb.Web;

public sealed record SaveDraftRequest(string? Title, string ContentJson, string BaseVersion, string[]? Tags = null);

public static class VersionRoutes
{
    public static void MapVersionEndpoints(this RouteGroupBuilder api)
    {
        MapDrafts(api);

        api.MapGet("/pages/{id:guid}/versions", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListVersions { PageId = id }, ct).ToHttp()).WithAction<ListVersions>();

        api.MapGet("/pages/{id:guid}/versions/{version}", (Guid id, string version, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadVersion { PageId = id, Version = version }, ct).ToHttp()).WithAction<ReadVersion>();

        api.MapGet("/pages/{id:guid}/diff", (Guid id, string from, string to, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DiffPage { PageId = id, From = from, To = to }, ct).ToHttp()).WithAction<DiffPage>();

        api.MapPost("/pages/{id:guid}/versions/{version}/restore", (Guid id, string version, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RestoreVersion { PageId = id, Version = version }, ct).ToHttp()).WithAction<RestoreVersion>();
    }

    private static void MapDrafts(RouteGroupBuilder api)
    {
        api.MapGet("/pages/{id:guid}/draft", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadDraft { PageId = id }, ct).ToHttp()).WithAction<ReadDraft>();

        api.MapPut("/pages/{id:guid}/draft", (Guid id, SaveDraftRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveDraft { PageId = id, Title = req.Title, ContentJson = req.ContentJson, BaseVersion = req.BaseVersion, Tags = req.Tags }, ct)
                .ToHttp()).WithAction<SaveDraft>();

        api.MapDelete("/pages/{id:guid}/draft", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DiscardDraft { PageId = id }, ct).ToHttp()).WithAction<DiscardDraft>();
    }
}
