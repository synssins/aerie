using System.Globalization;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

internal sealed class AddAttachmentHandler(KbDbContext db, IAccess access, IFileStore store, TimeProvider time) : IActionHandler<AddAttachment, AttachmentDto>
{
    private Page? _page;
    private long _max;

    public async Task<Problem?> AuthorizeAsync(ActionContext<AddAttachment> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is not { Kind: PageKind.Page }) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        if (ctx.Actor.UserId is null) return Problem.Unauthorized; // an attachment names who added it
        // The app's limit replaces the web server's here, before a plugin hook can read the file.
        // As before, a request that already says it is too large keeps the server's own limit; HandleAsync refuses it.
        _max = await UploadLimits.AttachmentBytesAsync(db, ctx.Cancel);
        if (ctx.Action.File is AttachmentUpload upload && !(upload.Length > _max)) upload.Allow(_max);
        return null;
    }

    public async Task<Outcome<AttachmentDto>> HandleAsync(ActionContext<AddAttachment> ctx)
    {
        var (page, me, max) = (_page!, ctx.Actor.UserId!.Value, _max);
        if (ctx.Action.File is not { } file) return Problem.Invalid("file", "Empty file.");
        if (file.Length > max) return TooLarge(max);
        var name = file.FileName;
        var a = new Attachment { PageId = page.Id, FileName = string.IsNullOrWhiteSpace(name) ? "file" : name[..Math.Min(name.Length, 255)], CreatedBy = me, CreatedAt = time.GetUtcNow() };
        var key = a.Id.ToString("N");
        // Streamed to the store, never held whole in memory. The file is still arriving, so this one write takes the token:
        // a request that goes away ends the upload, and the store keeps nothing of it.
        // The request's body is the request's to close; a file made by a plugin or an import is closed here.
        var body = await file.OpenReadAsync(ctx.Cancel);
        try { a.Size = await store.SaveAsync(key, body, max, ctx.Cancel); }
        finally { if (file is not AttachmentUpload) await body.DisposeAsync(); }
        if (a.Size < 0) return TooLarge(max);
        if (a.Size == 0)
        {
            await store.DeleteAsync(key, CancellationToken.None);
            return Problem.Invalid("file", "Empty file.");
        }
        db.Attachments.Add(a);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.AttachmentAdd, "attachment", a.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName, ["page"] = page.Id.ToString() });
        return new AttachmentDto(a.Id, a.FileName, a.Size, $"/api/v1/attachments/{a.Id}", a.CreatedAt);
    }

    private static Problem TooLarge(long max) => Problem.Invalid("file", $"{max / 1_048_576} MB maximum.");
}

internal sealed class ReadAttachmentHandler(KbDbContext db, IAccess access, IFileStore store, IHttpContextAccessor http) : IActionHandler<ReadAttachment, FileOut>
{
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadAttachment> ctx)
    {
        _attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ctx.Action.AttachmentId && x.DeletedAt == null, ctx.Cancel);
        return _attachment is null || await db.FindVisibleAsync(access, ctx.Actor, _attachment.PageId, track: false, ctx.Cancel) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<FileOut>> HandleAsync(ActionContext<ReadAttachment> ctx)
    {
        var a = _attachment!;
        var s = await store.OpenAsync(a.Id.ToString("N"), ctx.Cancel);
        if (s is null) return Problem.NotFound;
        // The answer closes the stream once it is sent. When a plugin's filter stops the answer, the end of the request closes it.
        http.HttpContext?.Response.RegisterForDispose(s);
        // Always a download, never shown inline: an attachment can be any file type.
        return new FileOut
        {
            ContentType = "application/octet-stream", FileName = a.FileName,
            Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'none'; sandbox" },
            Open = _ => Task.FromResult(s),
        };
    }
}

/// <summary>Removes it from the page. The file itself is kept.</summary>
internal sealed class RemoveAttachmentHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<RemoveAttachment, Unit>
{
    private Attachment? _attachment;
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveAttachment> ctx)
    {
        _attachment = await db.Attachments.FirstOrDefaultAsync(x => x.Id == ctx.Action.AttachmentId && x.DeletedAt == null, ctx.Cancel);
        if (_attachment is null || (_page = await db.FindVisibleAsync(access, ctx.Actor, _attachment.PageId, track: false, ctx.Cancel)) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who removed it
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveAttachment> ctx)
    {
        var (a, page) = (_attachment!, _page!);
        a.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.AttachmentRemove, "attachment", a.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName });
        return new Unit();
    }
}

// The upload settings are an Admin's: AuthorizeAsync answers 403 to anyone else. An Admin is always a signed-in person, so ctx.Actor.UserId is set in HandleAsync.

internal sealed class ReadUploadSettingsHandler(KbDbContext db, IAccess access, IWebHostEnvironment env) : IActionHandler<ReadUploadSettings, UploadSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadUploadSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<UploadSettingsDto>> HandleAsync(ActionContext<ReadUploadSettings> ctx) =>
        new UploadSettingsDto(await UploadLimits.AttachmentMbAsync(db, ctx.Cancel), UploadLimits.ServerLimit(env));
}

/// <summary>Saved even when it is above the web server's limit: the screen warns, and the smaller limit applies until the server's is raised.</summary>
internal sealed class SaveUploadSettingsHandler(KbDbContext db, IAccess access, IWebHostEnvironment env, TimeProvider time) : IActionHandler<SaveUploadSettings, UploadSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveUploadSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<UploadSettingsDto>> HandleAsync(ActionContext<SaveUploadSettings> ctx)
    {
        var mb = ctx.Action.AttachmentMb;
        if (mb is < 1 or > UploadLimits.MaxMb) return Problem.Invalid("attachmentMb", $"1 to {UploadLimits.MaxMb}.");
        var value = mb.ToString(CultureInfo.InvariantCulture);
        await db.SetAsync(UploadLimits.AttachmentKey, value, ctx.Actor.UserId, time.GetUtcNow());
        ctx.Audit(AuditActions.SettingsChange, "setting", UploadLimits.AttachmentKey, data: new Dictionary<string, string> { ["value"] = value });
        return new UploadSettingsDto(mb, UploadLimits.ServerLimit(env));
    }
}
