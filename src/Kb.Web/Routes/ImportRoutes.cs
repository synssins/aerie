using Kb.Web.Actions;
using Kb.Web.Import;
namespace Kb.Web;

public static class ImportRoutes
{
    public static void MapImportEndpoints(this RouteGroupBuilder api)
    {
        var imp = api.MapGroup("/imports");

        imp.MapPost("", (ImportBatchRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateImportBatch { SpaceId = req.SpaceId, ParentId = req.ParentId }, ct).ToHttp(b => Results.Created($"/api/v1/imports/{b.Id}", b))).WithAction<CreateImportBatch>();

        // One file a request, as an attachment: the body is the file, X-File-Name its path inside the upload (a folder keeps its paths).
        imp.MapPost("/{id:guid}/files", (Guid id, HttpContext http, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AddImportFile { BatchId = id, File = new AttachmentUpload(http.Request) }, ct).ToHttp()).WithAction<AddImportFile>();

        imp.MapPost("/{id:guid}/start", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new StartImportBatch { BatchId = id }, ct).ToHttp()).WithAction<StartImportBatch>();

        imp.MapGet("", (IActionBus bus, CancellationToken ct) => bus.SendAsync(new ListImports(), ct).ToHttp()).WithAction<ListImports>();

        imp.MapGet("/items/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadImportItem { ItemId = id }, ct).ToHttp()).WithAction<ReadImportItem>();

        imp.MapGet("/files/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadImportFile { FileId = id }, ct).ToHttp()).WithAction<ReadImportFile>();

        imp.MapPost("/items/{id:guid}/accept", (Guid id, AcceptRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AcceptImport { ItemId = id, Request = req }, ct).ToHttp()).WithAction<AcceptImport>();

        imp.MapPost("/items/{id:guid}/reject", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RejectImport { ItemId = id }, ct).ToHttp()).WithAction<RejectImport>();

        imp.MapPost("/items/{id:guid}/retry", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RetryImport { ItemId = id }, ct).ToHttp()).WithAction<RetryImport>();

        imp.MapDelete("/items/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DiscardImport { ItemId = id }, ct).ToHttp()).WithAction<DiscardImport>();

        imp.MapPost("/merge", (ImportMergeRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new MergeImports { ItemIds = req.ItemIds ?? [] }, ct).ToHttp()).WithAction<MergeImports>();

        api.MapPost("/pages/{id:guid}/format", (Guid id, FormatRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new FormatPage { PageId = id, ContentJson = req.ContentJson ?? "" }, ct).ToHttp()).WithAction<FormatPage>();
    }
}
