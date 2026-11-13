using System.Text;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

internal sealed class ExportPageHandler(KbDbContext db, IAccess access, IFileStore store) : IActionHandler<ExportPage, FileOut>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ExportPage> ctx) =>
        (_page = await ExportSupport.PageAsync(db, access, ctx.Actor, ctx.Action.PageId, ctx.Cancel)) is null ? Problem.NotFound : null;

    public async Task<Outcome<FileOut>> HandleAsync(ActionContext<ExportPage> ctx)
    {
        var page = _page!;
        var fmt = (ctx.Action.Format ?? "pdf").ToLowerInvariant();
        if (fmt is not ("pdf" or "docx")) return Problem.Invalid("format", "pdf or docx.");
        var blocks = DocText.Blocks(page.ContentJson);

        // Only this page's own stored images, in formats the document can hold.
        var images = new Dictionary<Guid, ExportImage>();
        foreach (var imageId in DocText.ImageIds(page.ContentJson))
        {
            var meta = await db.SpaceImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ctx.Cancel);
            if (meta is null) continue;
            await using var s = await store.OpenAsync(imageId.ToString("N"), ctx.Cancel);
            if (s is null) continue;
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ctx.Cancel);
            var data = ms.ToArray();
            // shortcut: WebP images export as a "[image]" note; convert them on upload if that becomes common.
            if (Exports.ImageSize(data) is { Width: > 0, Height: > 0 } size) images[imageId] = new ExportImage(data, meta.ContentType, size.Width, size.Height);
        }

        var bytes = fmt == "pdf" ? Exports.Pdf(page.Title, blocks, images) : Exports.Docx(page.Title, blocks, images);
        ExportSupport.Audit(ctx, page, fmt);
        return ExportSupport.File(bytes, fmt == "pdf" ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{Exports.FileName(page.Title)}.{fmt}");
    }
}

internal sealed class ExportPageTableHandler(KbDbContext db, IAccess access) : IActionHandler<ExportPageTable, FileOut>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ExportPageTable> ctx) =>
        (_page = await ExportSupport.PageAsync(db, access, ctx.Actor, ctx.Action.PageId, ctx.Cancel)) is null ? Problem.NotFound : null;

    public Task<Outcome<FileOut>> HandleAsync(ActionContext<ExportPageTable> ctx)
    {
        var (page, n) = (_page!, ctx.Action.Number);
        var tables = DocText.Blocks(page.ContentJson).OfType<DocTable>().ToList();
        if (n < 1 || n > tables.Count) return Task.FromResult<Outcome<FileOut>>(Problem.NotFound);
        var fmt = (ctx.Action.Format ?? "csv").ToLowerInvariant();
        if (fmt is not ("csv" or "xlsx")) return Task.FromResult<Outcome<FileOut>>(Problem.Invalid("format", "csv or xlsx."));
        var t = tables[n - 1];
        ExportSupport.Audit(ctx, page, $"table-{fmt}");
        var name = $"{Exports.FileName(page.Title)}-table-{n}.{fmt}";
        return Task.FromResult<Outcome<FileOut>>(fmt == "csv"
            ? ExportSupport.File(Exports.Csv(t.Rows), "text/csv", name)
            : ExportSupport.File(Exports.Xlsx(t.Rows, t.HeaderRow), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name));
    }
}

internal sealed class ExportPageCodeHandler(KbDbContext db, IAccess access) : IActionHandler<ExportPageCode, FileOut>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ExportPageCode> ctx) =>
        (_page = await ExportSupport.PageAsync(db, access, ctx.Actor, ctx.Action.PageId, ctx.Cancel)) is null ? Problem.NotFound : null;

    public Task<Outcome<FileOut>> HandleAsync(ActionContext<ExportPageCode> ctx)
    {
        var (page, n) = (_page!, ctx.Action.Number);
        var blocks = DocText.CodeBlocks(page.ContentJson);
        if (n < 1 || n > blocks.Count) return Task.FromResult<Outcome<FileOut>>(Problem.NotFound);
        var b = blocks[n - 1];
        ExportSupport.Audit(ctx, page, "code");
        return Task.FromResult<Outcome<FileOut>>(
            ExportSupport.File(Encoding.UTF8.GetBytes(b.Text), "application/octet-stream", $"{Exports.FileName(b.Heading ?? page.Title, "code")}.{Exports.ExtensionFor(b.Language)}"));
    }
}

file static class ExportSupport
{
    /// <summary>The page, when the actor may read it. A folder has nothing to export: null, like a page that is not there.</summary>
    public static async Task<Page?> PageAsync(KbDbContext db, IAccess access, Actor actor, Guid id, CancellationToken ct) =>
        await db.FindVisibleAsync(access, actor, id, track: false, ct) is { Kind: PageKind.Page } page ? page : null;

    /// <summary>Every export is audited under one name; the format says which it was.</summary>
    public static void Audit(ActionContext ctx, Page page, string format) =>
        ctx.Audit(AuditActions.PageExport, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["format"] = format, ["version"] = page.Version });

    /// <summary>A download. The file is whole in memory, as before, so its length is part of the answer.</summary>
    public static FileOut File(byte[] bytes, string contentType, string name) =>
        new() { ContentType = contentType, FileName = name, Open = _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)) };
}
