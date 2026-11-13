namespace Kb.Web;

public static class ExportRoutes
{
    public static void MapExportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/pages/{id:guid}/export", (Guid id, string? format, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ExportPage { PageId = id, Format = format }, ct).ToHttp()).WithAction<ExportPage>();

        api.MapGet("/pages/{id:guid}/export/table/{n:int}", (Guid id, int n, string? format, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ExportPageTable { PageId = id, Number = n, Format = format }, ct).ToHttp()).WithAction<ExportPageTable>();

        api.MapGet("/pages/{id:guid}/export/code/{n:int}", (Guid id, int n, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ExportPageCode { PageId = id, Number = n }, ct).ToHttp()).WithAction<ExportPageCode>();
    }
}
