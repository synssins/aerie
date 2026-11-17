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
/// <summary>
/// Source: the first file's name (and how many more). SuggestedSpace: the space the AI suggested, by name. UpdatedAt: when a
/// Working item started drafting. DraftMs: how long the model took.
/// </summary>
public sealed record ImportItemRowDto(Guid Id, Guid BatchId, string Title, string Status, string? Error, int Files, DateTimeOffset CreatedAt, Guid? PageId,
    string? Source = null, string? SuggestedSpace = null, DateTimeOffset? UpdatedAt = null, long? DraftMs = null);
/// <summary>CanImport, CanFormat: whether the AI features are open to the caller (the app shows them only then).</summary>
public sealed record ImportListDto(IReadOnlyList<ImportBatchDto> Batches, IReadOnlyList<ImportItemRowDto> Items, bool CanImport = false, bool CanFormat = false,
    IReadOnlyList<ImportSpaceDto>? Spaces = null, bool CanAssist = false);
/// <summary>A space the caller may add pages to, with its default review cycle: where an import may go.</summary>
public sealed record ImportSpaceDto(Guid Id, string Name, bool IsSensitive, int? ReviewMonths);
/// <summary>Proposal: the AI's draft and what the code found (JSON, see ImportService.ProposeAsync). Sources: the text read from each file.</summary>
public sealed record ImportItemDto(Guid Id, string Title, string Status, string? Error, JsonNode? Proposal, IReadOnlyList<ImportFileDto> Files, IReadOnlyDictionary<Guid, string> Sources, Guid? PageId,
    IReadOnlyList<ImportSpaceDto>? Spaces = null);
public sealed record ImportMergeRequest(IReadOnlyList<Guid>? ItemIds);
/// <summary>Roles and Keep: to build the preview again without the model (to keep a duplicate it removed), the roles it gave.</summary>
public sealed record FormatRequest(string? ContentJson, IReadOnlyList<FormatRoleDto>? Roles = null, IReadOnlyList<int>? Keep = null);
/// <summary>Split: where a paragraph that runs items together is cut, as the text each item after the first starts with.
/// Fix: the spelling and grammar corrections in the part.</summary>
public sealed record FormatRoleDto(int N, string Role, IReadOnlyList<string>? Split = null, IReadOnlyList<FormatFixDto>? Fix = null);
public sealed record FormatFixDto(string From, string To);
/// <summary>Removed: the duplicates taken out, each with what says the same and stays. Roles: what the model gave, to build it again.</summary>
public sealed record FormatDto(string ContentJson, IReadOnlyList<DiffLine> Diff, IReadOnlyList<FormatRemovedDto>? Removed = null, IReadOnlyList<FormatRoleDto>? Roles = null);
public sealed record FormatRemovedDto(IReadOnlyList<int> Units, string Text, string SameAs);

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

/// <summary>Sets a draft aside: nothing is made, its files stay, and it can be drafted again.</summary>
[Action("import.reject")]
public sealed record RejectImport : IAction<Unit>
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
    public IReadOnlyList<FormatRoleDto>? Roles { get; init; }
    public IReadOnlyList<int>? Keep { get; init; }
}

internal static class ImportGate
{
    internal static async Task<Problem?> MayAsync(ImportService imports, Actor actor, CancellationToken ct) =>
        actor.UserId is null ? Problem.Unauthorized
        : actor.TokenId is not null ? new Problem(403, "Import is done in the app, not with an API token.")
        : await imports.MayImportAsync(actor, ct) ? null : new Problem(403, "Import is not open to you.");

    /// <summary>The spaces the caller may create pages in, whatever role gives them that (a built-in or a custom one).</summary>
    internal static async Task<IReadOnlyList<ImportSpaceDto>> WritableAsync(KbDbContext db, IAccess access, Actor actor, CancellationToken ct)
    {
        var writable = new List<Guid>();
        foreach (var s in await access.VisibleSpacesAsync(actor, ct)) if (await access.CanAsync(actor, s, SpaceAction.CreateEdit, ct)) writable.Add(s);
        return await db.Spaces.AsNoTracking().Where(s => writable.Contains(s.Id)).OrderBy(s => s.Name)
            .Select(s => new ImportSpaceDto(s.Id, s.Name, s.IsSensitive, s.ReviewMonths)).ToListAsync(ct);
    }

    internal static ImportItemRowDto Row(ImportItem i, int files, string? source = null, string? suggestedSpace = null)
    {
        long? ms = null;
        if (i.ProposalJson is { } pj && JsonNode.Parse(pj)?["ms"] is JsonValue v && v.TryGetValue<long>(out var n)) ms = n;
        return new(i.Id, i.BatchId, i.Title, i.Status.ToString(), i.Error, files, i.CreatedAt, i.PageId, source, suggestedSpace, i.UpdatedAt, ms);
    }
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

internal sealed class ListImportsHandler(KbDbContext db, ImportService imports, IAccess access) : IActionHandler<ListImports, ImportListDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListImports> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<ImportListDto>> HandleAsync(ActionContext<ListImports> ctx)
    {
        var me = ctx.Actor.UserId!.Value;
        var items = await db.ImportItems.AsNoTracking().Where(i => i.UserId == me && i.Status != ImportStatus.Discarded).OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync(ctx.Cancel);
        var ids = items.Select(i => i.Id).ToList();
        // What each item is, at a glance: its first file and the space the AI suggested.
        var names = (await db.ImportFiles.AsNoTracking().Where(f => f.ItemId != null && ids.Contains(f.ItemId.Value) && !f.Embedded).Select(f => new { f.ItemId, f.Path, f.Name }).ToListAsync(ctx.Cancel))
            .GroupBy(f => f.ItemId!.Value).ToDictionary(g => g.Key, g => { var first = g.OrderBy(f => f.Path, StringComparer.Ordinal).First().Name; return g.Count() > 1 ? $"{first} +{g.Count() - 1}" : first; });
        Guid? Suggested(ImportItem i) => i.ProposalJson is { } pj && JsonNode.Parse(pj)?["suggestedDestination"]?["spaceId"] is JsonValue v && v.TryGetValue<Guid>(out var g) ? g : null;
        var suggested = items.ToDictionary(i => i.Id, Suggested);
        var spaceIds = suggested.Values.OfType<Guid>().Distinct().ToList();
        var spaceNames = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ctx.Cancel);
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
            [.. items.Select(i => ImportGate.Row(i, counts.GetValueOrDefault(i.Id), names.GetValueOrDefault(i.Id),
                suggested[i.Id] is { } sid ? spaceNames.GetValueOrDefault(sid) : null))],
            ctx.Actor.TokenId is null && await imports.MayImportAsync(ctx.Actor, ctx.Cancel), await imports.MayAsync(ctx.Actor, AiFeatures.Format, ctx.Cancel),
            await ImportGate.WritableAsync(db, access, ctx.Actor, ctx.Cancel), ctx.Actor.TokenId is null && await imports.MayAsync(ctx.Actor, AiFeatures.Assist, ctx.Cancel));
    }
}

internal sealed class ReadImportItemHandler(KbDbContext db, IAccess access) : IActionHandler<ReadImportItem, ImportItemDto>
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
            files.Where(f => f.Text is not null).ToDictionary(f => f.Id, f => f.Text!), i.PageId, await ImportGate.WritableAsync(db, access, ctx.Actor, ctx.Cancel));
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

internal sealed class AcceptImportHandler(KbDbContext db, ImportService imports, IActionBus bus, Kb.Web.Pipeline.ActorAccessor actors) : IActionHandler<AcceptImport, IReadOnlyList<Guid>>
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
        // The actions accepting sends say they come from this import: a hook reads Actor.Source "import" and Actor.ImportItemId.
        // Only the core sets them, here; a caller cannot.
        actors.Use(ctx.Actor.ForImport(_item.Id));
        try { return await imports.AcceptAsync(bus, ctx.Actor, _item, ctx.Action.Request, ctx.Cancel); }
        finally { actors.Use(ctx.Actor); }
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
        await imports.DropFilesAsync(i.Id, ctx.Actor.UserId, "discarded", ctx.Cancel);
        return new Unit();
    }
}

internal sealed class RejectImportHandler(KbDbContext db, TimeProvider time) : IActionHandler<RejectImport, Unit>
{
    private ImportItem? _item;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RejectImport> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        return (_item = await db.ImportItems.FirstOrDefaultAsync(i => i.Id == ctx.Action.ItemId && i.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RejectImport> ctx)
    {
        var i = _item!;
        if (i.Status is not (ImportStatus.Ready or ImportStatus.Failed)) return Problem.Conflict("Only a ready or failed draft is rejected.");
        (i.Status, i.UpdatedAt) = (ImportStatus.Rejected, time.GetUtcNow());
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.ImportReject, "importItem", i.Id.ToString(), null, new Dictionary<string, string> { ["title"] = i.Title });
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
        if (i.Status is not (ImportStatus.Failed or ImportStatus.Ready or ImportStatus.Rejected)) return Problem.Conflict("Only a failed, ready or rejected draft is drafted again.");
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
        "\"lines\" holds the parts of a document, each as [n] (kind) text. Give each one the role that makes the document easier to read: " +
        "\"heading1\" to \"heading3\" for a section title, \"step\" for a line of a numbered procedure, \"bullet\" for a line of a list, " +
        "\"code\" for commands and configuration, \"note\" for a warning, \"paragraph\" for the rest, \"keep\" to leave it as it is (list items and blocks usually stay). " +
        "When the same text is in the document twice (a paragraph, a step, a whole procedure written out again, a title repeated as a step), " +
        "give the repeat the role \"duplicate\" and keep the copy that reads best, with the title of a repeated section as a duplicate too. " +
        "A section title stuck to the end of a list item, as a line of its own, is a title: give it a heading role. " +
        "When a paragraph runs several separate items together, such as label and value pairs (\"Label: value. Other label: value.\"), " +
        "give it the role \"paragraph\" and in \"split\" the exact text each item after the first starts with, as written (usually its label): " +
        "each item then goes on a line of its own. " +
        "Also correct spelling and grammar: in \"fix\" give each correction in the part, as \"from\" (the misspelt word or short phrase exactly as written) " +
        "and \"to\" (its correction). Correct only clear mistakes: never reword, never change the meaning, a fact, a number or a name, and never touch code, " +
        "commands, paths, host names, identifiers, product names, URLs or quoted error messages. A word you are not sure about stays as written. " +
        "Return one role for every n.";
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
        if (await ai.PageRefusalAsync(ctx.Actor, AiFeatures.Format, _page, before) is { } refused) return refused;
        var source = JsonNode.Parse(before)!.AsObject();
        var units = DocFormat.Units(source);
        if (units.Count == 0) return new Problem(422, "There is nothing to format.");
        var roles = new Dictionary<int, string>();
        var splits = new Dictionary<int, IReadOnlyList<string>>();
        var fixes = new Dictionary<int, IReadOnlyList<DocFormat.Fix>>();
        if (ctx.Action.Roles is { } given)
        {
            // Built again from the roles of an earlier preview (to keep a duplicate): no model call. They only arrange the page's own units.
            foreach (var g in given)
                if (g.N >= 0 && g.N < units.Count && DocFormat.Roles.Contains(g.Role) && roles.TryAdd(g.N, g.Role))
                {
                    if (g.Split is { Count: > 0 } cut) splits[g.N] = [.. cut.Take(20)];
                    if (g.Fix is { Count: > 0 } fix) fixes[g.N] = [.. fix.Take(20).Select(f => new DocFormat.Fix(f.From ?? "", f.To ?? ""))];
                }
        }
        else
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["roles"] = new JsonObject
                    {
                        ["type"] = "array", ["maxItems"] = units.Count,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["n"] = new JsonObject { ["type"] = "integer" },
                                ["role"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. DocFormat.Roles.Select(r => (JsonNode)r)]) },
                                ["split"] = new JsonObject { ["type"] = "array", ["maxItems"] = 20, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 } },
                                ["fix"] = new JsonObject
                                {
                                    ["type"] = "array", ["maxItems"] = 20,
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject { ["from"] = new JsonObject { ["type"] = "string", ["maxLength"] = 100 }, ["to"] = new JsonObject { ["type"] = "string", ["maxLength"] = 100 } },
                                        ["required"] = new JsonArray("from", "to"), ["additionalProperties"] = false,
                                    },
                                },
                            },
                            ["required"] = new JsonArray("n", "role"), ["additionalProperties"] = false,
                        },
                    },
                },
                ["required"] = new JsonArray("roles"), ["additionalProperties"] = false,
            };
            var r = await ai.RunAsync(ctx.Actor, AiFeatures.Format, Task, new Dictionary<string, string> { ["lines"] = DocFormat.Lines(units) }, schema, ctx.Cancel);
            if (r.Problem is { } p) return p;
            foreach (var item in r.Value!.Output["roles"]!.AsArray().OfType<JsonObject>())
                if (item["n"]!.GetValue<int>() is var n && n >= 0 && n < units.Count && roles.TryAdd(n, item["role"]!.GetValue<string>()))
                {
                    if (item["split"] is JsonArray cut && cut.Count > 0) splits[n] = [.. cut.Select(x => x!.GetValue<string>())];
                    if (item["fix"] is JsonArray fix && fix.Count > 0) fixes[n] = [.. fix.Select(x => new DocFormat.Fix(x!["from"]!.GetValue<string>(), x["to"]!.GetValue<string>()))];
                }
        }
        // The page is rebuilt from its own units: the model names roles and duplicates, it writes nothing. A duplicate goes only when
        // what stays says the same.
        // A fix that is not a spelling or grammar correction (another word, a number, a rewording) changes content: nothing is used.
        if (fixes.SelectMany(f => f.Value).FirstOrDefault(f => DocFormat.FixError(f) is not null) is { } bad)
        {
            ctx.Audit(AuditActions.PageFormat, "page", _page!.Id.ToString(), _page.SpaceId, new Dictionary<string, string> { ["outcome"] = "content-changed", ["fix"] = $"{bad.From} > {bad.To}".Truncate(200) }, evenIfRefused: true);
            return new Problem(422, $"The formatting changed content (\"{bad.From.Truncate(60)}\" to \"{bad.To.Truncate(60)}\"), so it was not used.");
        }
        var (built, removed) = DocFormat.Restructure(source, roles, ctx.Action.Keep?.ToHashSet(), splits, fixes);
        var doc = built.ToJsonString();
        // Kept as a guard all the same: every word of the result is one of the page's (as corrected), in order.
        var corrected = DocFormat.Restructure(source, new Dictionary<int, string>(), null, null, fixes).Doc.ToJsonString();
        if (!DocFormat.OnlyPageWords(DocText.Plain(corrected, renderers), DocText.Plain(doc, renderers)))
        {
            ctx.Audit(AuditActions.PageFormat, "page", _page!.Id.ToString(), _page.SpaceId, new Dictionary<string, string> { ["outcome"] = "words-changed" }, evenIfRefused: true);
            return new Problem(422, "The formatting changed words, so it was not used.");
        }
        ctx.Audit(AuditActions.PageFormat, "page", _page!.Id.ToString(), _page.SpaceId, new Dictionary<string, string>
        {
            ["outcome"] = ctx.Action.Roles is null ? "preview" : "preview-again",
            ["duplicatesRemoved"] = removed.Sum(x => x.Units.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return new FormatDto(doc, LineDiff.Diff(DocText.MarkdownLines(before, renderers), DocText.MarkdownLines(doc, renderers)),
            [.. removed.Select(x => new FormatRemovedDto(x.Units, x.Text, x.SameAs))], [.. roles.OrderBy(x => x.Key).Select(x => new FormatRoleDto(x.Key, x.Value, splits.GetValueOrDefault(x.Key),
                fixes.TryGetValue(x.Key, out var f) ? [.. f.Select(y => new FormatFixDto(y.From, y.To))] : null))]);
    }
}
