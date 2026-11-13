using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kb.Web;

public sealed record UploadSettingsDto(int AttachmentMb, long? ServerLimitBytes);
public sealed record UploadSettingsRequest(int AttachmentMb);

public static class AttachmentEndpoints
{

    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/pages/{id:guid}/attachments", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Kind == PageKind.Page, ct);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId, ct) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var max = await UploadLimits.AttachmentBytesAsync(db, ct);
            if (http.Request.ContentLength > max) return TooLarge(max);
            // ASP.NET Core has its own 30 MB cap per request; the app's setting replaces it here.
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap) cap.MaxRequestBodySize = max;
            var name = Path.GetFileName(Uri.UnescapeDataString(http.Request.Headers["X-File-Name"].ToString()));
            var a = new Attachment { PageId = id, FileName = string.IsNullOrWhiteSpace(name) ? "file" : name[..Math.Min(name.Length, 255)], CreatedBy = me, CreatedAt = time.GetUtcNow() };
            var key = a.Id.ToString("N");
            a.Size = await store.SaveAsync(key, http.Request.Body, max, ct);
            if (a.Size < 0) return TooLarge(max);
            if (a.Size == 0)
            {
                await store.DeleteAsync(key, ct);
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Empty file."] });
            }
            db.Attachments.Add(a);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AttachmentAdd, me, "attachment", a.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName, ["page"] = id.ToString() }), ct);
            return Results.Created($"/api/v1/attachments/{a.Id}", new AttachmentDto(a.Id, a.FileName, a.Size, $"/api/v1/attachments/{a.Id}", a.CreatedAt));
        });

        // Always a download, never shown inline: an attachment can be any file type.
        api.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, CancellationToken ct) =>
        {
            var a = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
            var page = a is null ? null : await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == a.PageId, ct);
            if (a is null || page is null || await perms.GetRoleAsync(http.User.UserId(), page.SpaceId, ct) is null) return Results.NotFound();
            var s = await store.OpenAsync(a.Id.ToString("N"), ct);
            if (s is null) return Results.NotFound();
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.Stream(s, "application/octet-stream", a.FileName);
        });

        // Removes it from the page. The file itself is kept.
        api.MapDelete("/attachments/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
            var page = a is null ? null : await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == a.PageId, ct);
            if (a is null || page is null || await perms.GetRoleAsync(me, page.SpaceId, ct) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            a.DeletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AttachmentRemove, me, "attachment", id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName }), ct);
            return Results.NoContent();
        });

        var admin = api.AdminGroup();
        admin.MapGet("/uploads", async (KbDbContext db, IWebHostEnvironment env, CancellationToken ct) =>
            Results.Ok(new UploadSettingsDto(await UploadLimits.AttachmentMbAsync(db, ct), UploadLimits.ServerLimit(env))));

        // Saved even when it is above the web server's limit: the screen warns, and the smaller limit applies until the server's is raised.
        admin.MapPut("/uploads", async (UploadSettingsRequest req, HttpContext http, KbDbContext db, IWebHostEnvironment env, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (req.AttachmentMb is < 1 or > UploadLimits.MaxMb)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["attachmentMb"] = [$"1 to {UploadLimits.MaxMb}."] });
            var mb = req.AttachmentMb.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await db.SetAsync(UploadLimits.AttachmentKey, mb, http.User.UserId(), time.GetUtcNow(), ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.SettingsChange, http.User.UserId(), "setting", UploadLimits.AttachmentKey, Data: new Dictionary<string, string> { ["value"] = mb }), ct);
            return Results.Ok(new UploadSettingsDto(req.AttachmentMb, UploadLimits.ServerLimit(env)));
        });
    }

    private static IResult TooLarge(long max) => Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [$"{max / 1_048_576} MB maximum."] });
}
