using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Content;
using Kb.Core.DataCenter;
using Kb.Data;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.DataCenter;

/// <summary>
/// Attachments on the Data Center compatible surface (contracts/behaviour/attachments.md): aerie's own attachments, with the versions, media types
/// and comments the surface adds kept in DcAttachmentVersions. The current file is always stored under the attachment's own id, as aerie reads it.
/// </summary>
internal sealed class DcAttachmentService(KbDbContext db, IAccess access, IFileStore store, DcContentService content, TimeProvider time)
{
    private static readonly FileExtensionContentTypeProvider Types = new();

    /// <summary>The part's type, or one guessed from the file name when it is absent or application/octet-stream.</summary>
    public static string MediaType(string? sent, string fileName) =>
        !string.IsNullOrWhiteSpace(sent) && !sent.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) ? sent.Split(';')[0].Trim()
        : Types.TryGetContentType(fileName, out var guessed) ? guessed : "application/octet-stream";

    /// <summary>The attachment's versions, oldest first, without removed ones. One made in aerie has its version 1 here the first time it is read.</summary>
    public async Task<List<DcAttachmentVersion>> VersionsAsync(Attachment a, CancellationToken ct)
    {
        var rows = await db.DcAttachmentVersions.Where(v => v.AttachmentId == a.Id).OrderBy(v => v.Number).ToListAsync(ct);
        if (rows.Count == 0)
        {
            await DcFirstUse.AddAsync(db, async () =>
            {
                if (await db.DcAttachmentVersions.AnyAsync(v => v.AttachmentId == a.Id, ct)) return [];
                var first = new DcAttachmentVersion { AttachmentId = a.Id, Number = 1, BlobId = a.Id, Size = a.Size, MediaType = MediaType(null, a.FileName), CreatedBy = a.CreatedBy, CreatedAt = a.CreatedAt };
                db.DcAttachmentVersions.Add(first);
                return [first];
            }, ct);
            rows = await db.DcAttachmentVersions.Where(v => v.AttachmentId == a.Id).OrderBy(v => v.Number).ToListAsync(ct);
        }
        return [.. rows.Where(v => !v.Removed)];
    }

    /// <summary>The attachment under this id on the page, when the caller sees the page; current only unless asked for trashed too.</summary>
    public async Task<(Attachment Attachment, Page Page)?> FindAsync(Actor actor, string? id, Guid? onPage, bool trashedToo, CancellationToken ct)
    {
        if (id is not null && id.StartsWith("att", StringComparison.Ordinal)) id = id[3..]; // the prefix some clients add
        if (await content.AerieIdAsync(id, ct) is not { } aerie) return null;
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == aerie && (trashedToo || x.DeletedAt == null), ct);
        if (a is null || (onPage is { } p && a.PageId != p)) return null;
        var page = await content.FindAsync(actor, a.PageId.ToString(), ["current"], track: false, ct);
        return page is null ? null : (a, page);
    }

    public async Task<List<Attachment>> OfPageAsync(Guid pageId, CancellationToken ct) =>
        await db.Attachments.AsNoTracking().Where(a => a.PageId == pageId && a.DeletedAt == null).OrderBy(a => a.CreatedAt).ThenBy(a => a.FileName).ToListAsync(ct);

    // ---- the files of versions ----

    private async Task CopyAsync(Guid from, Guid to, CancellationToken ct)
    {
        await using var s = await store.OpenAsync(from.ToString("N"), ct) ?? throw new InvalidOperationException("The file of an attachment is missing.");
        await store.SaveAsync(to.ToString("N"), s, long.MaxValue, ct);
    }

    /// <summary>Keeps the current file of a version as its own, so the attachment's own key can take a new one.</summary>
    public async Task SetAsideAsync(DcAttachmentVersion current)
    {
        if (current.BlobId != current.AttachmentId) return;
        var kept = Guid.NewGuid();
        await CopyAsync(current.AttachmentId, kept, CancellationToken.None);
        current.BlobId = kept;
    }

    /// <summary>Stores an uploaded file under a new key; -1 when it is over <paramref name="max"/>.</summary>
    public async Task<(Guid Key, long Size)> StageAsync(Stream source, long max, CancellationToken ct)
    {
        var key = Guid.NewGuid();
        var size = await store.SaveAsync(key.ToString("N"), source, max, ct);
        return (key, size);
    }

    /// <summary>Makes a staged file the attachment's current one.</summary>
    public async Task PromoteAsync(Guid staged, Guid attachmentId)
    {
        await CopyAsync(staged, attachmentId, CancellationToken.None);
        await store.DeleteAsync(staged.ToString("N"), CancellationToken.None);
    }

    public Task<Stream?> OpenAsync(Guid blob, CancellationToken ct) => store.OpenAsync(blob.ToString("N"), ct);

    /// <summary>Takes the newest remaining version back as the current file after the current one was removed.</summary>
    public async Task RestoreCurrentAsync(Attachment a, DcAttachmentVersion now)
    {
        await CopyAsync(now.BlobId, a.Id, CancellationToken.None);
        now.BlobId = a.Id;
        var row = await db.Attachments.FirstAsync(x => x.Id == a.Id);
        row.Size = now.Size;
        await db.SaveChangesAsync();
    }

    // ---- shaping ----

    public async Task<JsonObject> JsonAsync(Actor actor, Attachment a, Page page, Expand expand, CancellationToken ct, bool top = false)
    {
        var id = await content.IdOfAsync(a.Id, ct);
        var versions = await VersionsAsync(a, ct);
        var current = versions[^1];
        var self = "/rest/api/content/" + id.ToString(CultureInfo.InvariantCulture);
        var o = new JsonObject
        {
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
            ["type"] = "attachment",
            ["status"] = a.DeletedAt is null ? "current" : "trashed",
            ["title"] = a.FileName,
            ["extensions"] = new JsonObject { ["mediaType"] = current.MediaType, ["fileSize"] = current.Size, ["comment"] = current.Comment },
        };
        var expandable = new JsonObject();
        async Task Field(string name, string path, Func<Expand, Task<JsonNode?>> value)
        {
            if (expand.Has(name)) o[name] = await value(expand.Inside(name));
            else expandable[name] = path;
        }
        await Field("container", "/rest/api/content/" + (await content.IdOfAsync(page.Id, ct)).ToString(CultureInfo.InvariantCulture), async e => await content.ContentAsync(actor, page, e, ct));
        await Field("space", "", async e => await content.SpaceJsonAsync(page.SpaceId, e, ct));
        await Field("version", "", async e => await VersionJsonAsync(a, current, e, ct));
        await Field("metadata", "", _ => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["mediaType"] = current.MediaType, ["comment"] = current.Comment,
            ["labels"] = new JsonObject { ["results"] = new JsonArray(), ["start"] = 0, ["limit"] = 200, ["size"] = 0 },
        }));
        o["_links"] = new JsonObject
        {
            ["self"] = content.Links.Abs(self), ["webui"] = "/p/" + page.Id, ["download"] = Download(id, null),
        };
        if (top) o["_links"]!["collection"] = "/rest/api/content";
        o["_expandable"] = expandable;
        return top ? content.Links.Top(o) : o;
    }

    /// <summary>The path, relative to the site, that returns a version's bytes (the current one without a version).</summary>
    public static string Download(long id, int? version) =>
        DcRest.DownloadRoot + "/" + id.ToString(CultureInfo.InvariantCulture) + (version is { } v ? "?version=" + v.ToString(CultureInfo.InvariantCulture) : "");

    private async Task<JsonObject> VersionJsonAsync(Attachment a, DcAttachmentVersion v, Expand expand, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == v.CreatedBy, ct);
        return new JsonObject
        {
            ["by"] = DcUserJson.Of(user is null ? new DcUser(v.CreatedBy, "", "", true, true) : DcUserData.View(user), content.Links, expand.Inside("by")),
            ["when"] = DcSpaceJson.Date(v.CreatedAt),
            ["message"] = v.Comment,
            ["number"] = v.Number,
            ["minorEdit"] = v.MinorEdit,
            ["hidden"] = false,
            ["_links"] = new JsonObject { ["self"] = content.Links.Abs("/rest/api/content/" + (await content.IdOfAsync(a.Id, ct)).ToString(CultureInfo.InvariantCulture) + "/version/" + v.Number.ToString(CultureInfo.InvariantCulture)) },
        };
    }

    /// <summary>One page of attachments, as a list inside another object or on its own.</summary>
    public async Task<JsonObject> PageOfAsync(Actor actor, Page page, IReadOnlyList<Attachment> all, Paging paging, Expand expand, string restPath, CancellationToken ct)
    {
        var results = new JsonArray();
        foreach (var a in all.Skip(paging.Start).Take(paging.Limit)) results.Add(await JsonAsync(actor, a, page, expand, ct));
        var links = new JsonObject { ["self"] = content.Links.Abs(restPath) };
        if (all.Count > paging.Start + paging.Limit) links["next"] = $"{restPath}?limit={paging.Limit}&start={paging.Start + paging.Limit}";
        if (paging.Start > 0) links["prev"] = $"{restPath}?limit={paging.Limit}&start={Math.Max(0, paging.Start - paging.Limit)}";
        return new JsonObject { ["results"] = results, ["start"] = paging.Start, ["limit"] = paging.Limit, ["size"] = results.Count, ["_links"] = links };
    }

    public DateTimeOffset Now => time.GetUtcNow();
    public KbDbContext Db => db;
    public IAccess Access => access;
}
