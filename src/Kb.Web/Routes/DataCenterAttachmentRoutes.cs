using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Config;
using Kb.Web.Actions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>Attachments and labels (contracts/behaviour/attachments.md, labels-properties.md), and the download link attachments carry.</summary>
public static class DcAttachmentRoutes
{
    /// <summary>
    /// The multipart parts of an upload. The web server's own cap is raised to the most an Admin may allow; the action holds each file to the
    /// configured limit (413). Null for a body that is not multipart.
    /// </summary>
    private static async Task<IFormCollection?> FormAsync(HttpRequest request)
    {
        if (!request.HasFormContentType || !(request.ContentType ?? "").StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return null;
        var cap = (long)UploadLimits.MaxMb * 1_048_576 + 1_048_576;
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = cap;
        return await request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = cap }, request.HttpContext.RequestAborted);
    }

    private static DcFilePart Part(IFormFile f) => new(Path.GetFileName(f.FileName), f.ContentType, f.Length, f.OpenReadStream);

    private static bool Flag(IFormCollection form, string name) => bool.TryParse(form[name].ToString(), out var b) && b;

    private static IResult NotMultipart => DcRest.Fail(415, "Send the files as multipart/form-data.");

    public static void MapDcAttachments(this RouteGroupBuilder dc)
    {
        dc.MapGet("/content/{id}/child/attachment", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var (links, q) = (DcLinks.Of(http), http.Request.Query);
            if (Paging.Read(http.Request, 50, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            return bus.SendAsync(new DcListAttachments
            {
                Id = id, FileName = q["filename"].ToString() is { Length: > 0 } f ? f : null, MediaType = q["mediaType"].ToString() is { Length: > 0 } m ? m : null,
                Start = paging.Start, Limit = paging.Limit, Expand = Expand.Raw(http.Request),
            }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcListAttachments>();

        dc.MapPost("/content/{id}/child/attachment", async (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var status = http.Request.Query["status"].ToString();
            if (status.Length > 0 && status is not ("current" or "draft")) return DcRest.Fail(400, "status is current or draft.");
            if (status == "draft") return DcRest.Fail(404, "Aerie has no drafts to attach to.");
            if (await FormAsync(http.Request) is not { } form) return NotMultipart;
            return await bus.SendAsync(new DcCreateAttachments
            {
                Id = id, Files = [.. form.Files.GetFiles("file").Select(Part)], Comments = [.. form["comment"].Select(c => c ?? "")],
                MinorEdit = Flag(form, "minorEdit"), AllowDuplicated = bool.TryParse(http.Request.Query["allowDuplicated"].ToString(), out var dup) && dup,
                Expand = Expand.Raw(http.Request),
            }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcCreateAttachments>();

        dc.MapPut("/content/{id}/child/attachment/{attachmentId}", async (HttpContext http, string id, string attachmentId, IActionBus bus, CancellationToken ct) =>
        {
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            return await bus.SendAsync(new DcUpdateAttachment { Id = id, AttachmentId = attachmentId, Body = body }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcUpdateAttachment>();

        dc.MapDelete("/content/{id}/child/attachment/{attachmentId}", (string id, string attachmentId, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcRemoveAttachment { Id = id, AttachmentId = attachmentId }, ct).Rest(_ => Results.NoContent())).WithAction<DcRemoveAttachment>();

        dc.MapPost("/content/{id}/child/attachment/{attachmentId}/data", async (HttpContext http, string id, string attachmentId, IActionBus bus, CancellationToken ct) =>
        {
            if (await FormAsync(http.Request) is not { } form) return NotMultipart;
            var parts = form.Files.GetFiles("file");
            if (parts.Count != 1) return DcRest.Fail(400, "Exactly one file part is needed.");
            return await bus.SendAsync(new DcAttachmentData
            {
                Id = id, AttachmentId = attachmentId, File = Part(parts[0]), Comment = form["comment"].ToString() is { Length: > 0 } c ? c : null, MinorEdit = Flag(form, "minorEdit"),
            }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcAttachmentData>();

        dc.MapPost("/content/{id}/child/attachment/{attachmentId}/move", (HttpContext http, string id, string attachmentId, IActionBus bus, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            return bus.SendAsync(new DcMoveAttachment
            {
                Id = id, AttachmentId = attachmentId, NewContentId = q["newContentId"].ToString() is { Length: > 0 } c ? c : null, NewName = q["newName"].ToString() is { Length: > 0 } n ? n : null,
            }, ct).Rest(_ => Results.NoContent());
        }).WithAction<DcMoveAttachment>();

        dc.MapDelete("/content/{id}/child/attachment/{attachmentId}/version/{version}", (string id, string attachmentId, string version, IActionBus bus, CancellationToken ct) =>
            int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1
                ? bus.SendAsync(new DcRemoveAttachmentVersion { Id = id, AttachmentId = attachmentId, Number = n }, ct).Rest(_ => Results.NoContent())
                : Task.FromResult(DcRest.Fail(400, "A version number is 1 or more."))).WithAction<DcRemoveAttachmentVersion>();

        dc.MapGet("/content/{id}/child/attachment/{attachmentId}/extractedtext", (HttpContext http, string id, string attachmentId, IActionBus bus, CancellationToken ct) =>
        {
            long? limit = null;
            if (http.Request.Query["limit"].ToString() is { Length: > 0 } l)
            {
                if (!long.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return Task.FromResult(DcRest.Fail(400, "limit is 1 or more."));
                limit = n;
            }
            return bus.SendAsync(new DcAttachmentText { Id = id, AttachmentId = attachmentId, Limit = limit }, ct)
                .Rest(text => text.Length == 0 ? Results.NoContent() : DcRest.Json(new JsonObject { ["extractedText"] = text }));
        }).WithAction<DcAttachmentText>();

        dc.MapGet("/content/{id}/label", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var links = DcLinks.Of(http);
            if (Paging.Read(http.Request, 200, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            return bus.SendAsync(new DcListLabels { Id = id, Prefixes = DcRest.Values(http.Request, "prefix"), Start = paging.Start, Limit = paging.Limit }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcListLabels>();

        dc.MapPost("/content/{id}/label", async (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct); }
            catch (System.Text.Json.JsonException) { return DcRest.BadBody; }
            if (body is null) return DcRest.BadBody;
            return await bus.SendAsync(new DcAddLabels { Id = id, Body = body }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcAddLabels>();

        // The query form exists because a path segment cannot carry a slash.
        dc.MapDelete("/content/{id}/label", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
            http.Request.Query["name"].ToString() is { Length: > 0 } name
                ? bus.SendAsync(new DcRemoveLabel { Id = id, Label = name }, ct).Rest(_ => Results.NoContent())
                : Task.FromResult(DcRest.Fail(400, "name is required."))).WithAction<DcRemoveLabel>();

        dc.MapDelete("/content/{id}/label/{label}", (string id, string label, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcRemoveLabel { Id = id, Label = label }, ct).Rest(_ => Results.NoContent())).WithAction<DcRemoveLabel>();
    }

    /// <summary>The bytes of an attachment (its _links.download). Outside /rest/api: it is a file, not an operation; the same sign-ins reach it.</summary>
    public static void MapDcDownload(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var context = app.Services.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter.ContextPath;
        app.MapGet(context + DcRest.DownloadRoot + "/{attachmentId}", (HttpContext http, string attachmentId, IActionBus bus, CancellationToken ct) =>
        {
            int? version = null;
            if (http.Request.Query["version"].ToString() is { Length: > 0 } v)
            {
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return Task.FromResult(DcRest.Fail(404, "No such version."));
                version = n;
            }
            return bus.SendAsync(new DcDownloadAttachment { AttachmentId = attachmentId, Version = version }, ct).Rest(file => Outcome.Of(file).ToHttp());
        }).AllowAnonymous().WithMetadata(DcDownloadRoute.Marker).WithAction<DcDownloadAttachment>();
    }
}

/// <summary>The two CQL searches (contracts/behaviour/search-cql.md).</summary>
public static class DcSearchRoutes
{
    public static void MapDcSearch(this RouteGroupBuilder dc)
    {
        Task<IResult> Search(HttpContext http, bool results, IActionBus bus, CancellationToken ct)
        {
            var (links, q) = (DcLinks.Of(http), http.Request.Query);
            if (Paging.Read(http.Request, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            if (q["cql"].ToString() is not { Length: > 0 } cql) return Task.FromResult(DcRest.Fail(400, "cql is required."));
            var excerpt = q["excerpt"].ToString() is { Length: > 0 } e ? e : "highlight";
            if (excerpt is not ("highlight" or "indexed" or "none")) return Task.FromResult(DcRest.Fail(400, "excerpt is highlight, indexed or none."));
            return bus.SendAsync(new DcSearch
            {
                Cql = cql, CqlContext = q["cqlcontext"].ToString() is { Length: > 0 } c ? c : null, Results = results,
                IncludeArchivedSpaces = bool.TryParse(q["includeArchivedSpaces"].ToString(), out var arch) && arch, Excerpt = excerpt,
                Start = paging.Start, Limit = paging.Limit, Expand = Expand.Raw(http.Request),
            }, ct).Rest(o => DcRest.Json(o));
        }
        dc.MapGet("/content/search", (HttpContext http, IActionBus bus, CancellationToken ct) => Search(http, false, bus, ct)).WithAction<DcSearch>();
        dc.MapGet("/search", (HttpContext http, IActionBus bus, CancellationToken ct) => Search(http, true, bus, ct)).WithAction<DcSearch>();
    }
}
