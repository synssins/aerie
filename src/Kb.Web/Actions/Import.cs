using System.Text.Json.Nodes;
using Kb.Core.Ai;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Import;
using Kb.Data;
using Kb.Web.Import;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// AI import and AI formatting. Core only: imports carry other people's files and the AI's drafts, which only their importer sees.

public sealed record ImportBatchRequest(Guid? SpaceId, Guid? ParentId);
public sealed record ImportBatchDto(Guid Id, DateTimeOffset CreatedAt, int Files, bool Started, bool Grouped, string? Error, int Queued, int Working, int Ready, int Failed, int Blocked, int Done);
public sealed record ImportFileDto(Guid Id, string Name, string Path, long Size, string Kind, string? Note, bool Embedded, string Sha256);
public sealed record ImportItemRowDto(Guid Id, Guid BatchId, string Title, string Status, string? Error, int Files, DateTimeOffset CreatedAt, Guid? PageId);
/// <summary>CanImport, CanFormat: whether the AI features are open to the caller (the app shows them only then).</summary>
public sealed record ImportListDto(IReadOnlyList<ImportBatchDto> Batches, IReadOnlyList<ImportItemRowDto> Items, bool CanImport = false, bool CanFormat = false);
/// <summary>Proposal: the AI's draft and what the code found (JSON, see ImportService.ProposeAsync). Sources: the text read from each file.</summary>
public sealed record ImportItemDto(Guid Id, string Title, string Status, string? Error, JsonNode? Proposal, IReadOnlyList<ImportFileDto> Files, IReadOnlyDictionary<Guid, string> Sources, Guid? PageId);
public sealed record ImportMergeRequest(IReadOnlyList<Guid>? ItemIds);
public sealed record FormatRequest(string? ContentJson);
public sealed record FormatDto(string ContentJson, IReadOnlyList<DiffLine> Diff);

[Action("import.batch.create")]
public sealed record CreateImportBatch : IAction<ImportBatchDto>
{
    public Guid? SpaceId { get; init; }
    public Guid? ParentId { get; init; }
}

[Action("import.file.add")]
public sealed record AddImportFile : IAction<ImportFileDto>
{
    public required Guid BatchId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public UploadContent? File { get; init; }
}

/// <summary>The upload is complete: the worker may read the batch.</summary>
[Action("import.batch.start")]
public sealed record StartImportBatch : IAction<ImportBatchDto>
{
    public required Guid BatchId { get; init; }
}

[Action("import.list", Read = true)]
public sealed record ListImports : IAction<ImportListDto>;

[Action("import.read", Read = true)]
public sealed record ReadImportItem : IAction<ImportItemDto>
{
    public required Guid ItemId { get; init; }
}

[Action("import.file.read", Read = true)]
public sealed record ReadImportFile : IAction<FileOut>
{
    public required Guid FileId { get; init; }
}

[Action("import.accept")]
public sealed record AcceptImport : IAction<IReadOnlyList<Guid>>
{
    public required Guid ItemId { get; init; }
    public required AcceptRequest Request { get; init; }
}

[Action("import.discard")]
public sealed record DiscardImport : IAction<Unit>
{
    public required Guid ItemId { get; init; }
}

[Action("import.retry")]
public sealed record RetryImport : IAction<Unit>
{
    public required Guid ItemId { get; init; }
}

/// <summary>Several items become one, read again by the AI.</summary>
[Action("import.merge")]
public sealed record MergeImports : IAction<ImportItemRowDto>
{
    public required IReadOnlyList<Guid> ItemIds { get; init; }
}

/// <summary>The AI formats a page's content: only the markup may change (words in equal words out). Answers a preview, saves nothing.</summary>
[Action("page.format")]
public sealed record FormatPage : IAction<FormatDto>
{
    public required Guid PageId { get; init; }
    public required string ContentJson { get; init; }
}

internal static class ImportGate
{
    internal static async Task<Problem?> MayAsync(ImportService imports, Actor actor, CancellationToken ct) =>
        actor.UserId is null ? Problem.Unauthorized
        : actor.TokenId is not null ? new Problem(403, "Import is done in the app, not with an API token.")
        : await imports.MayImportAsync(actor, ct) ? null : new Problem(403, "Import is not open to you.");

    internal static ImportItemRowDto Row(ImportItem i, int files) => new(i.Id, i.BatchId, i.Title, i.Status.ToString(), i.Error, files, i.CreatedAt, i.PageId);
}

internal sealed class CreateImportBatchHandler(KbDbContext db, ImportService imports, IAccess access, TimeProvider time) : IActionHandler<CreateImportBatch, ImportBatchDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateImportBatch> ctx)
    {
        if (await ImportGate.MayAsync(imports, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        if (ctx.Action.SpaceId is { } s)
        {
            if (await access.RoleAsync(ctx.Actor, s, ctx.Cancel) is null) return Problem.NotFound;
            if (!await access.CanAsync(ctx.Actor, s, SpaceAction.CreateEdit, ctx.Cancel)) return new Problem(403, "You cannot add pages there.");
        }
        return null;
    }

    public async Task<Outcome<ImportBatchDto>> HandleAsync(ActionContext<CreateImportBatch> ctx)
    {
        var req = ctx.Action;
        if (req.ParentId is { } parent && (req.SpaceId is not { } space || !await db.Pages.AnyAsync(p => p.Id == parent && p.SpaceId == space && p.Kind == PageKind.Folder, ctx.Cancel))) // system: the folder must be in the space
            return Problem.Invalid("parentId", "Must be a folder in the space.");
        var b = new ImportBatch { UserId = ctx.Actor.UserId!.Value, CreatedAt = time.GetUtcNow(), TargetSpaceId = req.SpaceId, TargetParentId = req.ParentId };
        db.ImportBatches.Add(b);
        await db.SaveChangesAsync(ctx.Cancel);
        return new ImportBatchDto(b.Id, b.CreatedAt, 0, false, false, null, 0, 0, 0, 0, 0, 0);
    }
}

internal sealed class AddImportFileHandler(KbDbContext db, ImportService imports) : IActionHandler<AddImportFile, ImportFileDto>
{
    private ImportBatch? _batch;
    private long _max;

    public async Task<Problem?> AuthorizeAsync(ActionContext<AddImportFile> ctx)
    {
        if (await ImportGate.MayAsync(imports, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        var me = ctx.Actor.UserId!.Value;
        if ((_batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == ctx.Action.BatchId && b.UserId == me, ctx.Cancel)) is null) return Problem.NotFound;
        _max = await UploadLimits.AttachmentBytesAsync(db, ctx.Cancel);
        if (ctx.Action.File is AttachmentUpload upload && !(upload.Length > _max)) upload.Allow(_max);
        return null;
    }

    public async Task<Outcome<ImportFileDto>> HandleAsync(ActionContext<AddImportFile> ctx)
    {
        var b = _batch!;
        if (b.CompletedAt is not null) return Problem.Conflict("This upload is already complete.");
        if (ctx.Action.File is not { } file) return Problem.Invalid("file", "Empty file.");
        if (file.Length > _max) return Problem.Invalid("file", $"{_max / 1_048_576} MB at most per file.");
        if (await db.ImportFiles.CountAsync(f => f.BatchId == b.Id, ctx.Cancel) >= ImportService.MaxFilesPerBatch) return Problem.Invalid("file", $"{ImportService.MaxFilesPerBatch} files at most in one upload.");
        using var ms = new MemoryStream();
        await (await file.OpenReadAsync(ctx.Cancel)).CopyToAsync(ms, ctx.Cancel);
        if (ms.Length > _max) return Problem.Invalid("file", $"{_max / 1_048_576} MB at most per file.");
        if (ms.Length == 0) return Problem.Invalid("file", "Empty file.");
        var raw = (ctx.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers["X-File-Name"].ToString()) ?? file.FileName ?? "file";
        var f = await imports.AddFileAsync(b, Uri.UnescapeDataString(raw), ms.ToArray(), ctx.Cancel);
        return new ImportFileDto(f.Id, f.Name, f.Path, f.Size, f.Kind, f.Note, f.Embedded, f.Sha256);
    }
}

internal sealed class StartImportBatchHandler(KbDbContext db, ImportService imports, TimeProvider time) : IActionHandler<StartImportBatch, ImportBatchDto>
{
    private ImportBatch? _batch;

    public async Task<Problem?> AuthorizeAsync(ActionContext<StartImportBatch> ctx)
    {
        if (await ImportGate.MayAsync(imports, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        var me = ctx.Actor.UserId!.Value;
        return (_batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == ctx.Action.BatchId && b.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<ImportBatchDto>> HandleAsync(ActionContext<StartImportBatch> ctx)
    {
        var b = _batch!;
        var files = await db.ImportFiles.CountAsync(f => f.BatchId == b.Id, ctx.Cancel);
        if (files == 0) return Problem.Invalid("files", "No files.");
        b.CompletedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ctx.Cancel);
        return new ImportBatchDto(b.Id, b.CreatedAt, files, true, b.Grouped, b.Error, 0, 0, 0, 0, 0, 0);
    }
}

internal sealed class ListImportsHandler(KbDbContext db, ImportService imports) : IActionHandler<ListImports, ImportListDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListImports> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<ImportListDto>> HandleAsync(ActionContext<ListImports> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        var items = await db.ImportItems.AsNoTracking().Where(i => i.UserId == me && i.Status != ImportStatus.Discarded).OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync(ctx.Cancel);
        var ids = items.Select(i => i.Id).ToList();
        var counts = await db.ImportFiles.AsNoTracking().Where(f => f.ItemId != null && ids.Contains(f.ItemId.Value) && !f.Embedded).GroupBy(f => f.ItemId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(g => g.Key!.Value, g => g.N, ctx.Cancel);
        var batches = await db.ImportBatches.AsNoTracking().Where(b => b.UserId == me).OrderByDescending(b => b.CreatedAt).Take(20).ToListAsync(ctx.Cancel);
        var batchIds = batches.Select(b => b.Id).ToList();
        var fileCounts = await db.ImportFiles.AsNoTracking().Where(f => batchIds.Contains(f.BatchId) && f.ParentId == null).GroupBy(f => f.BatchId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.N, ctx.Cancel);
        var statuses = await db.ImportItems.AsNoTracking().Where(i => batchIds.Contains(i.BatchId)).GroupBy(i => new { i.BatchId, i.Status }).Select(g => new { g.Key.BatchId, g.Key.Status, N = g.Count() })
            .ToListAsync(ctx.Cancel);
        int N(Guid b, params ImportStatus[] s) => statuses.Where(x => x.BatchId == b && s.Contains(x.Status)).Sum(x => x.N);
        return new ImportListDto(
            [.. batches.Select(b => new ImportBatchDto(b.Id, b.CreatedAt, fileCounts.GetValueOrDefault(b.Id), b.CompletedAt is not null, b.Grouped, b.Error,
                N(b.Id, ImportStatus.Queued), N(b.Id, ImportStatus.Working), N(b.Id, ImportStatus.Ready), N(b.Id, ImportStatus.Failed), N(b.Id, ImportStatus.Blocked), N(b.Id, ImportStatus.Accepted, ImportStatus.Discarded)))],
            [.. items.Select(i => ImportGate.Row(i, counts.GetValueOrDefault(i.Id)))],
            ctx.Actor.TokenId is null && await imports.MayImportAsync(ctx.Actor, ctx.Cancel), await imports.MayAsync(ctx.Actor, AiFeatures.Format, ctx.Cancel));
    }
}

internal sealed class ReadImportItemHandler(KbDbContext db) : IActionHandler<ReadImportItem, ImportItemDto>
{
    private ImportItem? _item;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadImportItem> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        return (_item = await db.ImportItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == ctx.Action.ItemId && i.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<ImportItemDto>> HandleAsync(ActionContext<ReadImportItem> ctx)
    {
        var i = _item!;
        var files = await db.ImportFiles.AsNoTracking().Where(f => f.ItemId == i.Id).OrderBy(f => f.Path).ToListAsync(ctx.Cancel);
        return new ImportItemDto(i.Id, i.Title, i.Status.ToString(), i.Error, i.ProposalJson is null ? null : JsonNode.Parse(i.ProposalJson),
            [.. files.Select(f => new ImportFileDto(f.Id, f.Name, f.Path, f.Size, f.Kind, f.Note, f.Embedded, f.Sha256))],
            files.Where(f => f.Text is not null).ToDictionary(f => f.Id, f => f.Text!), i.PageId);
    }
}

internal sealed class ReadImportFileHandler(KbDbContext db, IFileStore store) : IActionHandler<ReadImportFile, FileOut>
{
    private ImportFile? _file;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadImportFile> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        return (_file = await db.ImportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == ctx.Action.FileId && f.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<FileOut>> HandleAsync(ActionContext<ReadImportFile> ctx)
    {
        var f = _file!;
        if (await store.SizeAsync(ImportService.Key(f.Id), ctx.Cancel) is null) return Problem.NotFound;
        // Only a picture is shown inline; anything else downloads.
        var image = f.Kind == "Image" && f.Mime is { } m && m.StartsWith("image/", StringComparison.Ordinal) && m != "image/svg+xml";
        return new FileOut
        {
            ContentType = image ? f.Mime! : "application/octet-stream", FileName = image ? null : f.Name,
            Headers = new Dictionary<string, string> { ["X-Content-Type-Options"] = "nosniff", ["Cache-Control"] = "private, max-age=3600" },
            Open = async ct => await store.OpenAsync(ImportService.Key(f.Id), ct) ?? Stream.Null,
        };
    }
}

internal sealed class AcceptImportHandler(KbDbContext db, ImportService imports, IActionBus bus) : IActionHandler<AcceptImport, IReadOnlyList<Guid>>
{
    private ImportItem? _item;

    public async Task<Problem?> AuthorizeAsync(ActionContext<AcceptImport> ctx)
    {
        if (await ImportGate.MayAsync(imports, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        var me = ctx.Actor.UserId!.Value;
        return (_item = await db.ImportItems.FirstOrDefaultAsync(i => i.Id == ctx.Action.ItemId && i.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<IReadOnlyList<Guid>>> HandleAsync(ActionContext<AcceptImport> ctx)
    {
        if (_item!.Status != ImportStatus.Ready) return Problem.Conflict("Only a ready draft can be accepted.");
        return await imports.AcceptAsync(bus, ctx.Actor, _item, ctx.Action.Request, ctx.Cancel);
    }
}

internal sealed class DiscardImportHandler(KbDbContext db, ImportService imports, TimeProvider time) : IActionHandler<DiscardImport, Unit>
{
    private ImportItem? _item;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DiscardImport> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        return (_item = await db.ImportItems.FirstOrDefaultAsync(i => i.Id == ctx.Action.ItemId && i.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DiscardImport> ctx)
    {
        var i = _item!;
        if (i.Status is ImportStatus.Working) return Problem.Conflict("It is being drafted; try again in a moment.");
        (i.Status, i.UpdatedAt, i.ProposalJson) = (ImportStatus.Discarded, time.GetUtcNow(), null);
        await db.SaveChangesAsync(ctx.Cancel);
        await imports.DropFilesAsync(i.Id, ctx.Cancel);
        return new Unit();
    }
}

internal sealed class RetryImportHandler(KbDbContext db, ImportService imports, TimeProvider time) : IActionHandler<RetryImport, Unit>
{
    private ImportItem? _item;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RetryImport> ctx)
    {
        if (await ImportGate.MayAsync(imports, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        var me = ctx.Actor.UserId!.Value;
        return (_item = await db.ImportItems.FirstOrDefaultAsync(i => i.Id == ctx.Action.ItemId && i.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RetryImport> ctx)
    {
        var i = _item!;
        if (i.Status is not (ImportStatus.Failed or ImportStatus.Ready)) return Problem.Conflict("Only a failed or ready draft is drafted again.");
        (i.Status, i.Error, i.UpdatedAt) = (ImportStatus.Queued, null, time.GetUtcNow());
        await db.SaveChangesAsync(ctx.Cancel);
        return new Unit();
    }
}

internal sealed class MergeImportsHandler(KbDbContext db, ImportService imports, TimeProvider time) : IActionHandler<MergeImports, ImportItemRowDto>
{
    private List<ImportItem> _items = [];

    public async Task<Problem?> AuthorizeAsync(ActionContext<MergeImports> ctx)
    {
        if (await ImportGate.MayAsync(imports, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        var me = ctx.Actor.UserId!.Value;
        var ids = ctx.Action.ItemIds.Distinct().ToList();
        _items = await db.ImportItems.Where(i => ids.Contains(i.Id) && i.UserId == me).ToListAsync(ctx.Cancel);
        return _items.Count != ids.Count ? Problem.NotFound : null;
    }

    public async Task<Outcome<ImportItemRowDto>> HandleAsync(ActionContext<MergeImports> ctx)
    {
        if (_items.Count < 2) return Problem.Invalid("itemIds", "Two or more.");
        if (_items.Any(i => i.Status is not (ImportStatus.Ready or ImportStatus.Failed or ImportStatus.Queued))) return Problem.Conflict("Only drafts that are not being worked on can be merged.");
        var now = time.GetUtcNow();
        var merged = new ImportItem { BatchId = _items[0].BatchId, UserId = _items[0].UserId, Title = _items[0].Title, Status = ImportStatus.Queued, CreatedAt = now, UpdatedAt = now };
        db.ImportItems.Add(merged);
        var ids = _items.Select(i => i.Id).ToList();
        foreach (var f in await db.ImportFiles.Where(f => f.ItemId != null && ids.Contains(f.ItemId.Value)).ToListAsync(ctx.Cancel)) f.ItemId = merged.Id;
        foreach (var i in _items) (i.Status, i.ProposalJson, i.UpdatedAt) = (ImportStatus.Discarded, null, now);
        await db.SaveChangesAsync(ctx.Cancel);
        return ImportGate.Row(merged, await db.ImportFiles.CountAsync(f => f.ItemId == merged.Id && !f.Embedded, ctx.Cancel));
    }
}

internal sealed class FormatPageHandler(KbDbContext db, IAccess access, ImportService imports, AiService ai, DocRenderers renderers) : IActionHandler<FormatPage, FormatDto>
{
    private const string Task =
        "\"lines\" holds the paragraphs of a document, each as [n] text. Give each one the role that makes the document easier to read: " +
        "\"heading1\" to \"heading3\" for section titles, \"step\" for each line of a numbered procedure, \"bullet\" for each line of a list, " +
        "\"code\" for commands and configuration, \"note\" for warnings, \"paragraph\" for the rest. Return one role for every n.";
    private Kb.Core.Content.Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<FormatPage> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is not { Kind: PageKind.Page }) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        return await imports.MayAsync(ctx.Actor, AiFeatures.Format, ctx.Cancel) ? null : new Problem(403, "Formatting is not open to you.");
    }

    public async Task<Outcome<FormatDto>> HandleAsync(ActionContext<FormatPage> ctx)
    {
        var before = ctx.Action.ContentJson;
        if (!PageSupport.IsEditorDoc(before)) return Problem.Invalid("contentJson", "Not an editor document.");
        var source = JsonNode.Parse(before)!.AsObject();
        var lines = DocBuilder.ParagraphLines(source);
        if (lines.Count == 0) return new Problem(422, "There is no plain text to format.");
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["roles"] = new JsonObject
                {
                    ["type"] = "array", ["maxItems"] = lines.Count,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["n"] = new JsonObject { ["type"] = "integer" }, ["role"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. DocBuilder.Roles.Select(r => (JsonNode)r)]) } },
                        ["required"] = new JsonArray("n", "role"), ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JsonArray("roles"), ["additionalProperties"] = false,
        };
        var data = new Dictionary<string, string> { ["lines"] = string.Join('\n', lines.Select((l, n) => $"[{n}] {l.Replace('\n', ' ')}")) };
        var r = await ai.RunAsync(ctx.Actor, AiFeatures.Format, Task, data, schema, ctx.Cancel);
        if (r.Problem is { } p) return p;
        // The model only names a role for each paragraph; the page is rebuilt from its own paragraphs, so no word can change.
        var roles = new Dictionary<int, string>();
        foreach (var item in r.Value!.Output["roles"]!.AsArray().OfType<JsonObject>())
            if (item["n"]!.GetValue<int>() is var n && n >= 0 && n < lines.Count) roles.TryAdd(n, item["role"]!.GetValue<string>());
        var doc = DocBuilder.Restructure(source, roles).ToJsonString();
        // Kept as a guard all the same: the same words in the same order, or nothing is offered.
        if (!AiGuard.SameWords(DocText.Plain(before, renderers), DocText.Plain(doc, renderers)))
        {
            ctx.Audit(AuditActions.PageFormat, "page", _page!.Id.ToString(), _page.SpaceId, new Dictionary<string, string> { ["outcome"] = "words-changed" }, evenIfRefused: true);
            return new Problem(422, "The formatting changed words, so it was not used.");
        }
        ctx.Audit(AuditActions.PageFormat, "page", _page!.Id.ToString(), _page.SpaceId, new Dictionary<string, string> { ["outcome"] = "preview" });
        return new FormatDto(doc, LineDiff.Diff(DocText.MarkdownLines(before, renderers), DocText.MarkdownLines(doc, renderers)));
    }
}
