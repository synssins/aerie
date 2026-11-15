using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.DataCenter;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web.DataCenter;
using Kb.Web.Import;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Attachments and labels on the Data Center compatible surface (contracts/behaviour/attachments.md, labels-properties.md). A new attachment is
// aerie's own (attachment.add, sent from inside); a new version, new properties, a move and a version removal are written here, audited,
// with the current file kept under the attachment's own id so aerie's download keeps serving it. Labels are aerie's page tags.

/// <summary>One file part of a multipart upload. Its file name is null when the part named none (attachments.md, Q-0018).</summary>
public sealed class DcFilePart(string? fileName, string? contentType, long length, Func<Stream> open) : UploadContent
{
    public override string? FileName => fileName;
    public string? ContentType => contentType;
    public override long? Length => length;
    public override Task<Stream> OpenReadAsync(CancellationToken ct) => Task.FromResult(open());
}

[Action("dc.attachment.list", Read = true)]
public sealed record DcListAttachments : IAction<JsonObject>
{
    public required string Id { get; init; }
    public string? FileName { get; init; }
    public string? MediaType { get; init; }
    public required int Start { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.attachment.create")]
public sealed record DcCreateAttachments : IAction<JsonObject>
{
    public required string Id { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<DcFilePart> Files { get; init; } = [];
    public IReadOnlyList<string> Comments { get; init; } = [];
    public bool MinorEdit { get; init; }
    public bool AllowDuplicated { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.attachment.data")]
public sealed record DcAttachmentData : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required string AttachmentId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public DcFilePart? File { get; init; }
    public string? Comment { get; init; }
    public bool MinorEdit { get; init; }
}

[Action("dc.attachment.update")]
public sealed record DcUpdateAttachment : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required string AttachmentId { get; init; }
    public required JsonObject Body { get; init; }
}

[Action("dc.attachment.move")]
public sealed record DcMoveAttachment : IAction<Unit>
{
    public required string Id { get; init; }
    public required string AttachmentId { get; init; }
    public string? NewContentId { get; init; }
    public string? NewName { get; init; }
}

[Action("dc.attachment.remove")]
public sealed record DcRemoveAttachment : IAction<Unit>
{
    public required string Id { get; init; }
    public required string AttachmentId { get; init; }
}

[Action("dc.attachment.version.remove")]
public sealed record DcRemoveAttachmentVersion : IAction<Unit>
{
    public required string Id { get; init; }
    public required string AttachmentId { get; init; }
    public required int Number { get; init; }
}

[Action("dc.attachment.text", Read = true)]
public sealed record DcAttachmentText : IAction<string>
{
    public required string Id { get; init; }
    public required string AttachmentId { get; init; }
    public long? Limit { get; init; }
}

[Action("dc.attachment.download", Read = true)]
public sealed record DcDownloadAttachment : IAction<FileOut>
{
    public required string AttachmentId { get; init; }
    public int? Version { get; init; }
}

[Action("dc.label.list", Read = true)]
public sealed record DcListLabels : IAction<JsonObject>
{
    public required string Id { get; init; }
    public IReadOnlyList<string> Prefixes { get; init; } = [];
    public required int Start { get; init; }
    public required int Limit { get; init; }
}

[Action("dc.label.add")]
public sealed record DcAddLabels : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required JsonNode Body { get; init; }
}

[Action("dc.label.remove")]
public sealed record DcRemoveLabel : IAction<Unit>
{
    public required string Id { get; init; }
    public required string Label { get; init; }
}

internal static partial class DcAttachmentRules
{
    public static readonly Problem NotFound = new(404, "No attachment with this id is on this content.");

    /// <summary>The content in the path when the caller may write attachments to it: 404 unseen, 403 no edit right, archived space 403.</summary>
    public static async Task<(Page? Page, Problem? Problem)> WritableAsync(DcContentService content, IAccess access, Actor actor, string id, CancellationToken ct)
    {
        if (actor.UserId is null) return (null, Problem.Unauthorized);
        if (await content.FindAsync(actor, id, ["current"], track: false, ct) is not { } page) return (null, DcContentRules.NotFound);
        if (await content.ArchivedAsync(page.SpaceId, ct)) return (null, DcContentRules.Archived);
        return await access.CanAsync(actor, page.SpaceId, SpaceAction.CreateEdit, ct) ? (page, null) : (null, new Problem(403, "You may not change attachments here."));
    }

    public static Problem TooLarge(long max) => new(413, $"A file is at most {max / 1_048_576} MB.");

    /// <summary>A label written as prefix:name is split; without a prefix it is global (labels-properties.md).</summary>
    public static (string Prefix, string Name) Split(string label, string? prefix = null)
    {
        var colon = label.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && prefix is null ? (label[..colon], label[(colon + 1)..]) : (prefix ?? "global", label);
    }

    /// <summary>aerie's rule (labels-properties.md): lower case, no whitespace or : ; , . ? &amp; [ ] ( ) # ^ * @ ! &lt; &gt;, at most 255.</summary>
    [GeneratedRegex(@"^[^\s:;,.?&\[\]()#^*@!<>]{1,255}$")]
    public static partial Regex LabelName();
}

internal sealed class DcListAttachmentsHandler(DcContentService content, DcAttachmentService files) : IActionHandler<DcListAttachments, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcListAttachments> ctx) =>
        (_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current"], track: false, ctx.Cancel)) is null ? DcContentRules.NotFound : null;

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcListAttachments> ctx)
    {
        var req = ctx.Action;
        var all = await files.OfPageAsync(_page!.Id, ctx.Cancel);
        if (req.FileName is { } name) all = [.. all.Where(a => a.FileName == name)];
        if (req.MediaType is { } type)
        {
            var kept = new List<Attachment>();
            foreach (var a in all) if (string.Equals((await files.VersionsAsync(a, ctx.Cancel))[^1].MediaType, type, StringComparison.OrdinalIgnoreCase)) kept.Add(a);
            all = kept;
        }
        var path = $"/rest/api/content/{await content.IdOfAsync(_page.Id, ctx.Cancel)}/child/attachment";
        return content.Links.Top(await files.PageOfAsync(ctx.Actor, _page, all, new Paging(req.Start, req.Limit), Expand.Parse(req.Expand), path, ctx.Cancel));
    }
}

internal sealed class DcCreateAttachmentsHandler(KbDbContext db, IAccess access, DcContentService content, DcAttachmentService files, IActionBus bus, IFileStore store, TimeProvider time) : IActionHandler<DcCreateAttachments, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcCreateAttachments> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcCreateAttachments> ctx)
    {
        var (req, page, me) = (ctx.Action, _page!, ctx.Actor.UserId!.Value);
        if (req.Files.Count == 0) return new Problem(400, "At least one file part is needed.");
        if (req.Comments.Count > 0 && req.Comments.Count != req.Files.Count) return new Problem(400, "Send one comment per file, or none.");
        var max = await UploadLimits.AttachmentBytesAsync(db, ctx.Cancel);
        if (req.Files.Any(f => f.Length > max)) return DcAttachmentRules.TooLarge(max);
        var existing = await files.OfPageAsync(page.Id, ctx.Cancel);
        // A part without a file name gets a name of its own after it is made, so it never clashes (Q-0018).
        var named = req.Files.Where(f => f.FileName is not null).ToList();
        if (!req.AllowDuplicated && named.FirstOrDefault(f => existing.Any(a => a.FileName == f.FileName)) is { } clash)
            return new Problem(400, $"{clash.FileName} is already attached; send allowDuplicated=true for a new version.");
        if (named.Select(f => f.FileName).Distinct().Count() < named.Count) return new Problem(400, "Two files in the request have the same name.");

        var made = new List<Attachment>();
        for (var i = 0; i < req.Files.Count; i++)
        {
            var (file, comment) = (req.Files[i], req.Comments.Count > 0 ? req.Comments[i] : "");
            // Without a file name the part's Content-Type is ignored: the media type is application/octet-stream (attachments.md).
            var media = file.FileName is null ? "application/octet-stream" : DcAttachmentService.MediaType(file.ContentType, file.FileName);
            if (file.FileName is not null && existing.FirstOrDefault(a => a.FileName == file.FileName) is { } same)
            {
                if (await NewVersionAsync(ctx, files, db, same, page, file, file.FileName!, media, comment, req.MinorEdit, max) is { } p) return p;
                made.Add(same);
                continue;
            }
            Attachment a;
            if (page.Kind == PageKind.Page)
            {
                var added = await bus.SendAsync(new AddAttachment { PageId = page.Id, File = file }, CancellationToken.None);
                if (added.Problem is { } ap) return ap.Fields?.ContainsKey("file") == true && ap.Fields["file"][0].Contains("MB", StringComparison.Ordinal) ? DcAttachmentRules.TooLarge(max) : ap;
                a = await db.Attachments.FirstAsync(x => x.Id == added.Value!.Id, CancellationToken.None);
            }
            else if (await ToFolderAsync(ctx, page, file, max) is { } folderFile) a = folderFile;
            else return DcAttachmentRules.TooLarge(max);
            db.DcAttachmentVersions.Add(new DcAttachmentVersion { AttachmentId = a.Id, Number = 1, BlobId = a.Id, Size = a.Size, MediaType = media, Comment = comment, MinorEdit = req.MinorEdit, CreatedBy = me, CreatedAt = a.CreatedAt });
            await db.SaveChangesAsync(CancellationToken.None);
            if (file.FileName is null)
            {
                // "attachment-" and the new attachment's id: it never clashes with a name already there; the client can rename it later.
                a.FileName = "attachment-" + (await content.IdOfAsync(a.Id, CancellationToken.None)).ToString(CultureInfo.InvariantCulture);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            made.Add(a);
        }
        var results = new JsonArray();
        foreach (var a in made) results.Add(await files.JsonAsync(ctx.Actor, a, page, Expand.Parse(req.Expand), CancellationToken.None));
        return content.Links.Top(new JsonObject { ["results"] = results, ["start"] = 0, ["limit"] = results.Count, ["size"] = results.Count });
    }

    /// <summary>
    /// A file attached to a folder (attachments.md, Q-0006: clients cannot tell folders from pages, so both take attachments). Aerie's own
    /// attachment.add takes pages only, so the file is stored here, as that action stores it, and audited the same way. Null when too large.
    /// </summary>
    private async Task<Attachment?> ToFolderAsync(ActionContext ctx, Page folder, DcFilePart file, long max)
    {
        var name = file.FileName ?? "attachment";
        var a = new Attachment { PageId = folder.Id, FileName = name[..Math.Min(name.Length, 255)], CreatedBy = ctx.Actor.UserId!.Value, CreatedAt = time.GetUtcNow() };
        a.Size = await store.SaveAsync(a.Id.ToString("N"), await file.OpenReadAsync(CancellationToken.None), max, CancellationToken.None);
        if (a.Size < 0) return null;
        db.Attachments.Add(a);
        await db.SaveChangesAsync(CancellationToken.None);
        ctx.Audit(AuditActions.AttachmentAdd, "attachment", a.Id.ToString(), folder.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName, ["page"] = folder.Id.ToString() });
        return a;
    }

    /// <summary>A new version of an attachment with this file: the current one is set aside under a key of its own, the new one takes the attachment's key.</summary>
    internal static async Task<Problem?> NewVersionAsync(ActionContext ctx, DcAttachmentService files, KbDbContext db, Attachment a, Page page, DcFilePart file, string name, string media,
        string comment, bool minor, long max)
    {
        var (staged, size) = await files.StageAsync(await file.OpenReadAsync(CancellationToken.None), max, CancellationToken.None);
        if (size < 0) return DcAttachmentRules.TooLarge(max);
        if (size == 0) return new Problem(400, $"{name} is empty.");
        var versions = await files.VersionsAsync(a, CancellationToken.None);
        await files.SetAsideAsync(versions[^1]);
        await files.PromoteAsync(staged, a.Id);
        var row = await db.Attachments.FirstAsync(x => x.Id == a.Id);
        (row.Size, row.FileName) = (size, name[..Math.Min(name.Length, 255)]);
        var number = await db.DcAttachmentVersions.Where(v => v.AttachmentId == a.Id).MaxAsync(v => v.Number) + 1;
        db.DcAttachmentVersions.Add(new DcAttachmentVersion { AttachmentId = a.Id, Number = number, BlobId = a.Id, Size = size, MediaType = media, Comment = comment, MinorEdit = minor,
            CreatedBy = ctx.Actor.UserId!.Value, CreatedAt = files.Now });
        await db.SaveChangesAsync();
        (a.Size, a.FileName) = (row.Size, row.FileName);
        ctx.Audit(AuditActions.AttachmentVersion, "attachment", a.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = row.FileName, ["version"] = number.ToString(CultureInfo.InvariantCulture) });
        return null;
    }
}

internal sealed class DcAttachmentDataHandler(KbDbContext db, IAccess access, DcContentService content, DcAttachmentService files) : IActionHandler<DcAttachmentData, JsonObject>
{
    private Page? _page;
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcAttachmentData> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        if (problem is not null) return problem;
        _attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, _page!.Id, trashedToo: false, ctx.Cancel))?.Attachment;
        return _attachment is null ? DcAttachmentRules.NotFound : null;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcAttachmentData> ctx)
    {
        var (req, page, a) = (ctx.Action, _page!, _attachment!);
        if (req.File is not { } file) return new Problem(400, "Exactly one file part is needed.");
        var max = await UploadLimits.AttachmentBytesAsync(db, ctx.Cancel);
        if (file.Length > max) return DcAttachmentRules.TooLarge(max);
        // The file name in the part becomes the title; it may not take another attachment's name on this content. A part without one keeps the
        // title and the media type as they are, and its Content-Type is ignored (attachments.md, Q-0018).
        if (file.FileName is not null && file.FileName != a.FileName && (await files.OfPageAsync(page.Id, ctx.Cancel)).Any(x => x.FileName == file.FileName)) return new Problem(400, $"{file.FileName} is already attached here.");
        var (name, media) = file.FileName is { } given ? (given, DcAttachmentService.MediaType(file.ContentType, given)) : (a.FileName, (await files.VersionsAsync(a, ctx.Cancel))[^1].MediaType);
        if (await DcCreateAttachmentsHandler.NewVersionAsync(ctx, files, db, a, page, file, name, media, req.Comment ?? "", req.MinorEdit, max) is { } p)
            return p;
        return await files.JsonAsync(ctx.Actor, a, page, Expand.None, CancellationToken.None, top: true);
    }
}

internal sealed class DcUpdateAttachmentHandler(KbDbContext db, IAccess access, DcContentService content, DcAttachmentService files) : IActionHandler<DcUpdateAttachment, JsonObject>
{
    private Page? _page;
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcUpdateAttachment> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        if (problem is not null) return problem;
        _attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, _page!.Id, trashedToo: false, ctx.Cancel))?.Attachment;
        return _attachment is null ? DcAttachmentRules.NotFound : null;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcUpdateAttachment> ctx)
    {
        var (body, page, a) = (ctx.Action.Body, _page!, _attachment!);
        if (DcRest.Text(body, "type") != "attachment") return new Problem(400, "type must be attachment.");
        if (body["version"]?["number"] is not JsonValue nv || !nv.TryGetValue<int>(out var number)) return new Problem(400, "version.number is required.");
        var versions = await files.VersionsAsync(a, ctx.Cancel);
        var current = versions[^1];
        if (number != current.Number + 1) return new Problem(409, $"version.number must be {current.Number + 1}, one more than the current version.");
        var name = DcRest.Text(body, "title")?.Trim() is { Length: > 0 } t ? t : a.FileName;
        if (name.Length > 255) return new Problem(400, "A file name is at most 255 characters.");
        var target = page;
        var containerId = body["container"]?["id"] is JsonValue cv ? cv.ToString() : null;
        if (containerId is not null)
        {
            if (await content.FindAsync(ctx.Actor, containerId, ["current"], track: false, ctx.Cancel) is not { } to) return new Problem(404, "No content with that id is visible to you.");
            if (await content.ArchivedAsync(to.SpaceId, ctx.Cancel) || !await access.CanAsync(ctx.Actor, to.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return new Problem(403, "You may not add attachments there.");
            target = to;
        }
        if ((name != a.FileName || target.Id != page.Id) && (await files.OfPageAsync(target.Id, ctx.Cancel)).Any(x => x.Id != a.Id && x.FileName == name))
            return new Problem(400, $"{name} is already attached there.");

        // The bytes stay; a new version records the change.
        await files.SetAsideAsync(current);
        var row = await db.Attachments.FirstAsync(x => x.Id == a.Id, CancellationToken.None);
        (row.FileName, row.PageId) = (name, target.Id);
        db.DcAttachmentVersions.Add(new DcAttachmentVersion
        {
            AttachmentId = a.Id, Number = number, BlobId = a.Id, Size = row.Size,
            MediaType = DcRest.Text(body, "metadata.mediaType") is { Length: > 0 } m ? m : current.MediaType,
            Comment = DcRest.Text(body, "metadata.comment") ?? current.Comment,
            MinorEdit = body["version"]?["minorEdit"] is JsonValue mv && mv.TryGetValue<bool>(out var minor) && minor,
            CreatedBy = ctx.Actor.UserId!.Value, CreatedAt = files.Now,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        ctx.Audit(target.Id != page.Id ? AuditActions.AttachmentMove : AuditActions.AttachmentUpdate, "attachment", a.Id.ToString(), target.SpaceId,
            new Dictionary<string, string> { ["name"] = name, ["page"] = target.Id.ToString() });
        return await files.JsonAsync(ctx.Actor, row, target, Expand.None, CancellationToken.None, top: true);
    }
}

internal sealed class DcMoveAttachmentHandler(KbDbContext db, IAccess access, DcContentService content, DcAttachmentService files) : IActionHandler<DcMoveAttachment, Unit>
{
    private Page? _page;
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcMoveAttachment> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        if (problem is not null) return problem;
        _attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, _page!.Id, trashedToo: false, ctx.Cancel))?.Attachment;
        return _attachment is null ? DcAttachmentRules.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DcMoveAttachment> ctx)
    {
        var (req, page, a) = (ctx.Action, _page!, _attachment!);
        if (req.NewContentId is null && string.IsNullOrWhiteSpace(req.NewName)) return new Problem(400, "Give newContentId, newName or both.");
        var target = page;
        if (req.NewContentId is { } to)
        {
            if (await content.FindAsync(ctx.Actor, to, ["current"], track: false, ctx.Cancel) is not { } found) return new Problem(404, "No content with that id is visible to you.");
            if (await content.ArchivedAsync(found.SpaceId, ctx.Cancel) || !await access.CanAsync(ctx.Actor, found.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return new Problem(403, "You may not add attachments there.");
            target = found;
        }
        var name = req.NewName?.Trim() is { Length: > 0 } n ? n : a.FileName;
        if (name.Length > 255) return new Problem(400, "A file name is at most 255 characters.");
        if ((await files.OfPageAsync(target.Id, ctx.Cancel)).Any(x => x.Id != a.Id && x.FileName == name)) return new Problem(400, $"{name} is already attached there.");
        var row = await db.Attachments.FirstAsync(x => x.Id == a.Id, CancellationToken.None);
        (row.FileName, row.PageId) = (name, target.Id);
        await db.SaveChangesAsync(CancellationToken.None);
        ctx.Audit(AuditActions.AttachmentMove, "attachment", a.Id.ToString(), target.SpaceId, new Dictionary<string, string> { ["name"] = name, ["page"] = target.Id.ToString(), ["fromPage"] = page.Id.ToString() });
        return new Unit();
    }
}

internal sealed class DcRemoveAttachmentHandler(IAccess access, DcContentService content, DcAttachmentService files, IActionBus bus) : IActionHandler<DcRemoveAttachment, Unit>
{
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcRemoveAttachment> ctx)
    {
        (var page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        if (problem is not null) return problem;
        _attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, page!.Id, trashedToo: false, ctx.Cancel))?.Attachment;
        return _attachment is null ? DcAttachmentRules.NotFound : null;
    }

    // Aerie's own removal: the attachment leaves its page, with every version; the files are kept.
    public Task<Outcome<Unit>> HandleAsync(ActionContext<DcRemoveAttachment> ctx) => bus.SendAsync(new RemoveAttachment { AttachmentId = _attachment!.Id }, CancellationToken.None);
}

internal sealed class DcRemoveAttachmentVersionHandler(KbDbContext db, IAccess access, DcContentService content, DcAttachmentService files) : IActionHandler<DcRemoveAttachmentVersion, Unit>
{
    private Page? _page;
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcRemoveAttachmentVersion> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        if (problem is not null) return problem;
        _attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, _page!.Id, trashedToo: false, ctx.Cancel))?.Attachment;
        return _attachment is null ? DcAttachmentRules.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DcRemoveAttachmentVersion> ctx)
    {
        var a = _attachment!;
        var versions = await files.VersionsAsync(a, ctx.Cancel);
        if (versions.FirstOrDefault(v => v.Number == ctx.Action.Number) is not { } v) return new Problem(404, "No such version.");
        if (versions.Count == 1) return new Problem(400, "The only version cannot be removed; remove the attachment instead.");
        // Taken off the surface; the file is kept, as aerie keeps the files of removed attachments.
        var row = await db.DcAttachmentVersions.FirstAsync(x => x.Id == v.Id, CancellationToken.None);
        if (row.BlobId == a.Id)
        {
            // The current version goes: its file is set aside first, then the newest remaining one becomes the current file again.
            await files.SetAsideAsync(row);
            await files.RestoreCurrentAsync(a, versions.Where(x => x.Number != row.Number).MaxBy(x => x.Number)!);
        }
        row.Removed = true;
        await db.SaveChangesAsync(CancellationToken.None);
        ctx.Audit(AuditActions.AttachmentVersionRemove, "attachment", a.Id.ToString(), _page!.SpaceId, new Dictionary<string, string> { ["version"] = v.Number.ToString(CultureInfo.InvariantCulture) });
        return new Unit();
    }
}

internal sealed class DcAttachmentTextHandler(DcContentService content, DcAttachmentService files) : IActionHandler<DcAttachmentText, string>
{
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcAttachmentText> ctx)
    {
        if (await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current"], track: false, ctx.Cancel) is not { } page) return DcContentRules.NotFound;
        _attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, page.Id, trashedToo: false, ctx.Cancel))?.Attachment;
        return _attachment is null ? DcAttachmentRules.NotFound : null;
    }

    /// <summary>The text aerie's import readers pull from the current file; empty when there is none (the route answers 204).</summary>
    public async Task<Outcome<string>> HandleAsync(ActionContext<DcAttachmentText> ctx)
    {
        var a = _attachment!;
        // shortcut: extracted on each request, and only files up to 20 MB; keep the text with the version if this is called often.
        if (a.Size > 20 * 1_048_576) return "";
        await using var s = await files.OpenAsync(a.Id, ctx.Cancel);
        if (s is null) return "";
        using var buffer = new MemoryStream();
        await s.CopyToAsync(buffer, ctx.Cancel);
        var extracted = Extractors.Extract(a.FileName, buffer.ToArray());
        var text = extracted.Kind == ExtractKind.Text ? extracted.Text.Trim() : "";
        return ctx.Action.Limit is { } limit && text.Length > limit ? text[..(int)Math.Min(limit, int.MaxValue)] : text;
    }
}

internal sealed class DcDownloadAttachmentHandler(DcAttachmentService files, IHttpContextAccessor http) : IActionHandler<DcDownloadAttachment, FileOut>
{
    private Attachment? _attachment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcDownloadAttachment> ctx) =>
        (_attachment = (await files.FindAsync(ctx.Actor, ctx.Action.AttachmentId, null, trashedToo: false, ctx.Cancel))?.Attachment) is null ? DcAttachmentRules.NotFound : null;

    public async Task<Outcome<FileOut>> HandleAsync(ActionContext<DcDownloadAttachment> ctx)
    {
        var a = _attachment!;
        var versions = await files.VersionsAsync(a, ctx.Cancel);
        var v = ctx.Action.Version is { } n ? versions.FirstOrDefault(x => x.Number == n) : versions[^1];
        if (v is null) return new Problem(404, "No such version.");
        var s = await files.OpenAsync(v.BlobId, ctx.Cancel);
        if (s is null) return Problem.NotFound;
        http.HttpContext?.Response.RegisterForDispose(s);
        // The stored type, as attachments.md says; as a download in a sandbox, so a stored page can never run as aerie.
        return new FileOut
        {
            ContentType = v.MediaType, FileName = a.FileName,
            Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'none'; sandbox" },
            Open = _ => Task.FromResult(s),
        };
    }
}

internal sealed class DcListLabelsHandler(KbDbContext db, DcContentService content) : IActionHandler<DcListLabels, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcListLabels> ctx) =>
        (_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current"], track: false, ctx.Cancel)) is null ? DcContentRules.NotFound : null;

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcListLabels> ctx) =>
        await DcLabels.PageAsync(db, content, _page!, ctx.Action.Prefixes, new Paging(ctx.Action.Start, ctx.Action.Limit), ctx.Cancel);
}

internal static class DcLabels
{
    /// <summary>A content item's labels, by name. Aerie's tags are all global: asking only for my or team labels gives none.</summary>
    public static async Task<JsonObject> PageAsync(KbDbContext db, DcContentService content, Page page, IReadOnlyList<string> prefixes, Paging paging, CancellationToken ct)
    {
        var tags = prefixes.Count > 0 && !prefixes.Contains("global") ? [] : await db.PageTags.AsNoTracking().Where(t => t.PageId == page.Id).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync(ct);
        var results = new JsonArray();
        foreach (var t in tags.Skip(paging.Start).Take(paging.Limit)) results.Add(await content.LabelAsync("global", t, ct));
        var path = $"/rest/api/content/{await content.IdOfAsync(page.Id, ct)}/label";
        var links = new JsonObject { ["self"] = content.Links.Abs(path) };
        if (tags.Count > paging.Start + paging.Limit) links["next"] = $"{path}?limit={paging.Limit}&start={paging.Start + paging.Limit}";
        if (paging.Start > 0) links["prev"] = $"{path}?limit={paging.Limit}&start={Math.Max(0, paging.Start - paging.Limit)}";
        return content.Links.Top(new JsonObject { ["results"] = results, ["start"] = paging.Start, ["limit"] = paging.Limit, ["size"] = results.Count, ["_links"] = links });
    }
}

internal sealed class DcAddLabelsHandler(KbDbContext db, IAccess access, DcContentService content, DocRenderers renderers) : IActionHandler<DcAddLabels, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcAddLabels> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcAddLabels> ctx)
    {
        var page = _page!;
        var items = ctx.Action.Body switch { JsonArray a => [.. a], JsonObject o => new List<JsonNode?> { o }, _ => null };
        if (items is null || items.Count == 0) return new Problem(400, "The body is a label or a list of labels.");
        var wanted = new List<string>();
        foreach (var item in items)
        {
            if (item is not JsonObject o || DcRest.Text(o, "name") is not { } raw) return new Problem(400, "Each label needs a name.");
            var (prefix, name) = DcAttachmentRules.Split(raw.Trim(), DcRest.Text(o, "prefix"));
            name = name.ToLowerInvariant();
            if (prefix is not ("global" or "my" or "team")) return new Problem(400, $"{prefix} is not a label prefix.");
            // Aerie's tags are shared by everyone: T1 holds global labels only (labels-properties.md).
            if (prefix != "global") return new Problem(400, "Aerie keeps global labels only.");
            if (!DcAttachmentRules.LabelName().IsMatch(name)) return new Problem(400, $"\"{name}\" is not a valid label name.");
            if (name.Length > 50) return new Problem(400, "A label is at most 50 characters in aerie.");
            wanted.Add(name);
        }
        var have = await db.PageTags.Where(t => t.PageId == page.Id).Select(t => t.Tag).ToListAsync(ctx.Cancel);
        var added = wanted.Distinct().Where(w => !have.Contains(w)).ToList();
        if (have.Count + added.Count > 20) return new Problem(400, "A page holds at most 20 labels in aerie.");
        if (added.Count > 0)
        {
            db.PageTags.AddRange(added.Select(t => new PageTag { PageId = page.Id, Tag = t }));
            var tracked = await db.Pages.FirstAsync(p => p.Id == page.Id, CancellationToken.None); // system: the caller may edit this page, checked above
            await SearchIndex.StageAsync(db, tracked, [.. have, .. added], renderers, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            ctx.Audit(AuditActions.PageLabels, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["added"] = string.Join(',', added) });
        }
        return await DcLabels.PageAsync(db, content, page, [], new Paging(0, 200), CancellationToken.None);
    }
}

internal sealed class DcRemoveLabelHandler(KbDbContext db, IAccess access, DcContentService content, DocRenderers renderers) : IActionHandler<DcRemoveLabel, Unit>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcRemoveLabel> ctx)
    {
        (_page, var problem) = await DcAttachmentRules.WritableAsync(content, access, ctx.Actor, ctx.Action.Id, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DcRemoveLabel> ctx)
    {
        var page = _page!;
        var (prefix, name) = DcAttachmentRules.Split(ctx.Action.Label.Trim());
        name = name.ToLowerInvariant();
        var tag = prefix == "global" ? await db.PageTags.FirstOrDefaultAsync(t => t.PageId == page.Id && t.Tag == name, ctx.Cancel) : null;
        if (tag is null) return new Problem(404, "This label is not on the content.");
        db.PageTags.Remove(tag);
        var tracked = await db.Pages.FirstAsync(p => p.Id == page.Id, CancellationToken.None); // system: the caller may edit this page, checked above
        var rest = await db.PageTags.Where(t => t.PageId == page.Id && t.Tag != name).Select(t => t.Tag).ToListAsync(CancellationToken.None);
        await SearchIndex.StageAsync(db, tracked, rest, renderers, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        ctx.Audit(AuditActions.PageLabels, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["removed"] = name });
        return new Unit();
    }
}
