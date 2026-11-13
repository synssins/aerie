using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = MigraDoc.DocumentObjectModel;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Kb.Web;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/pages/{id:guid}/export", async (Guid id, string? format, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, IAuditLog audit, CancellationToken ct) =>
        {
            if (await ReadablePageAsync(db, perms, http, id, ct) is not { } page) return Results.NotFound();
            var fmt = (format ?? "pdf").ToLowerInvariant();
            if (fmt is not ("pdf" or "docx")) return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["pdf or docx."] });
            var blocks = DocText.Blocks(page.ContentJson);

            // Only this page's own stored images, in formats the document can hold.
            var images = new Dictionary<Guid, ExportImage>();
            foreach (var imageId in DocText.ImageIds(page.ContentJson))
            {
                var meta = await db.SpaceImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ct);
                if (meta is null) continue;
                await using var s = await store.OpenAsync(imageId.ToString("N"), ct);
                if (s is null) continue;
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                var data = ms.ToArray();
                // shortcut: WebP images export as a "[image]" note; convert them on upload if that becomes common.
                if (Exports.ImageSize(data) is { Width: > 0, Height: > 0 } size) images[imageId] = new ExportImage(data, meta.ContentType, size.Width, size.Height);
            }

            var bytes = fmt == "pdf" ? Exports.Pdf(page.Title, blocks, images) : Exports.Docx(page.Title, blocks, images);
            await AuditAsync(audit, http, page, fmt, ct);
            return Results.File(bytes, fmt == "pdf" ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{Exports.FileName(page.Title)}.{fmt}");
        });

        api.MapGet("/pages/{id:guid}/export/table/{n:int}", async (Guid id, int n, string? format, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            if (await ReadablePageAsync(db, perms, http, id, ct) is not { } page) return Results.NotFound();
            var tables = DocText.Blocks(page.ContentJson).OfType<DocTable>().ToList();
            if (n < 1 || n > tables.Count) return Results.NotFound();
            var fmt = (format ?? "csv").ToLowerInvariant();
            if (fmt is not ("csv" or "xlsx")) return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["csv or xlsx."] });
            var t = tables[n - 1];
            await AuditAsync(audit, http, page, $"table-{fmt}", ct);
            var name = $"{Exports.FileName(page.Title)}-table-{n}.{fmt}";
            return fmt == "csv"
                ? Results.File(Exports.Csv(t.Rows), "text/csv", name)
                : Results.File(Exports.Xlsx(t.Rows, t.HeaderRow), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        });

        api.MapGet("/pages/{id:guid}/export/code/{n:int}", async (Guid id, int n, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            if (await ReadablePageAsync(db, perms, http, id, ct) is not { } page) return Results.NotFound();
            var blocks = DocText.CodeBlocks(page.ContentJson);
            if (n < 1 || n > blocks.Count) return Results.NotFound();
            var b = blocks[n - 1];
            await AuditAsync(audit, http, page, "code", ct);
            return Results.File(Encoding.UTF8.GetBytes(b.Text), "application/octet-stream", $"{Exports.FileName(b.Heading ?? page.Title, "code")}.{Exports.ExtensionFor(b.Language)}");
        });
    }

    private static async Task<Page?> ReadablePageAsync(KbDbContext db, ISpacePermissionService perms, HttpContext http, Guid id, CancellationToken ct)
    {
        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Kind == PageKind.Page, ct);
        return page is null || await perms.GetRoleAsync(http.User.UserId(), page.SpaceId, ct) is null ? null : page;
    }

    private static Task AuditAsync(IAuditLog audit, HttpContext http, Page page, string format, CancellationToken ct) =>
        audit.AppendAsync(new AuditEvent(AuditActions.PageExport, http.User.UserId(), "page", page.Id.ToString(), page.SpaceId,
            new Dictionary<string, string> { ["format"] = format, ["version"] = page.Version }), ct);
}
