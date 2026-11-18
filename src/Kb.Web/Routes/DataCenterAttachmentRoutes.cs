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
    /// <summary>The parts of a multipart upload: every part named file, and the text of the others.</summary>
    private sealed record Upload(List<DcFilePart> Files, Dictionary<string, List<string>> Fields)
    {
        public string? Text(string name) => Fields.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;
        public bool Flag(string name) => bool.TryParse(Text(name), out var b) && b;
        public List<string> All(string name) => Fields.TryGetValue(name, out var v) ? v : [];
    }

    /// <summary>A text part (comment, minorEdit) is short; a longer one is refused instead of being held in memory.</summary>
    private const int MaxTextPart = 64 * 1024;

    /// <summary>
    /// Reads a multipart upload section by section. A part named file is a file whether or not it has a file name (attachments.md, Q-0018):
    /// its bytes, as received, go to a temporary file, never through a text decoder. The web server's own cap is raised to the most an Admin
    /// may allow. A file part is read up to the configured limit and one byte more: reading stops there and the action answers 413.
    /// Form is null for a body that is not multipart, or that cannot be read; Refused is set for a text part that is too long.
    /// </summary>
    private static async Task<(Upload? Form, IResult? Refused)> ReadUploadAsync(HttpRequest request)
    {
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var boundary = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return (null, null);
        var cap = (long)UploadLimits.MaxMb * 1_048_576 + 1_048_576;
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = cap;
        var abort = request.HttpContext.RequestAborted;
        var maxFile = await UploadLimits.AttachmentBytesAsync(request.HttpContext);
        var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, request.Body) { BodyLengthLimit = cap };
        var upload = new Upload([], new Dictionary<string, List<string>>(StringComparer.Ordinal));
        try
        {
            while (await reader.ReadNextSectionAsync(abort) is { } section)
            {
                if (!Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                var name = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                if (name == "file")
                {
                    var path = Path.GetTempFileName();
                    request.HttpContext.Response.RegisterForDispose(new TempFile(path));
                    long length = 0;
                    await using (var target = File.Create(path))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while (length <= maxFile && (read = await section.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxFile + 1 - length)), abort)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), abort);
                            length += read;
                        }
                    }
                    var fileName = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                    upload.Files.Add(new DcFilePart(string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName), section.ContentType, length, () => File.OpenRead(path)));
                    if (length > maxFile) break; // too large: the rest of the body is not read
                    continue;
                }
                var chars = new char[MaxTextPart + 1];
                using var text = new StreamReader(section.Body, System.Text.Encoding.UTF8);
                var count = await text.ReadBlockAsync(chars.AsMemory(), abort);
                if (count > MaxTextPart) return (null, DcRest.Fail(400, $"The part {(name.Length > 50 ? name[..50] : name)} is longer than {MaxTextPart / 1024} KB."));
                if (!upload.Fields.TryGetValue(name, out var list)) upload.Fields[name] = list = [];
                list.Add(new string(chars, 0, count));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { return (null, null); }
        return (upload, null);
    }

    /// <summary>A part's temporary file, deleted when the response is done.</summary>
    private sealed class TempFile(string path) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(path); } catch (IOException) { } // a file still open is removed by the system's temp cleanup
        }
    }

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
            }, ct).Rest(o => DcRest.Json(links.Repage(o, paging)));
        }).WithAction<DcListAttachments>();

        dc.MapPost("/content/{id}/child/attachment", async (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var status = http.Request.Query["status"].ToString();
            if (status.Length > 0 && status is not ("current" or "draft")) return DcRest.Fail(400, "status is current or draft.");
            if (status == "draft") return DcRest.Fail(404, "Aerie has no drafts to attach to.");
            var (form, refused) = await ReadUploadAsync(http.Request);
            if (form is null) return refused ?? NotMultipart;
            return await bus.SendAsync(new DcCreateAttachments
            {
                Id = id, Files = form.Files, Comments = form.All("comment"),
                MinorEdit = form.Flag("minorEdit"), AllowDuplicated = bool.TryParse(http.Request.Query["allowDuplicated"].ToString(), out var dup) && dup,
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
            var (form, refused) = await ReadUploadAsync(http.Request);
            if (form is null) return refused ?? NotMultipart;
            if (form.Files.Count != 1) return DcRest.Fail(400, "Exactly one file part is needed.");
            return await bus.SendAsync(new DcAttachmentData
            {
                Id = id, AttachmentId = attachmentId, File = form.Files[0], Comment = form.Text("comment") is { Length: > 0 } c ? c : null, MinorEdit = form.Flag("minorEdit"),
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
            return bus.SendAsync(new DcListLabels { Id = id, Prefixes = DcRest.Values(http.Request, "prefix"), Start = paging.Start, Limit = paging.Limit }, ct).Rest(o => DcRest.Json(links.Repage(o, paging)));
        }).WithAction<DcListLabels>();

        dc.MapPost("/content/{id}/label", async (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            // The contract's body is one label or a list of them (labels-properties.md). A client may also send that JSON as a JSON string.
            if (await LabelsBodyAsync(http.Request, ct) is not { } body) return DcRest.Fail(400, "The body is a label object or a list of label objects, as JSON.");
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

    private static async Task<JsonNode?> LabelsBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = (await reader.ReadToEndAsync(ct)).Trim().TrimStart('﻿');
        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonValue v && v.TryGetValue<string>(out var inner)) node = JsonNode.Parse(inner);
            return node is JsonObject or JsonArray ? node : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
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
