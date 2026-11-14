using Microsoft.AspNetCore.Http.Features;

namespace Kb.Web;

public sealed record UploadSettingsRequest(int AttachmentMb);

/// <summary>The body of an upload request as a file. It goes to its reader as it arrives and is never held whole in memory.</summary>
internal sealed class AttachmentUpload(HttpRequest request) : UploadContent
{
    private bool _read;

    public override string? FileName => Path.GetFileName(Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString()));
    public override long? Length => request.ContentLength;

    /// <summary>ASP.NET Core has its own 30 MB cap per request; the app's setting replaces it. It can be set only before the body is read.</summary>
    public void Allow(long maxBytes)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap) cap.MaxRequestBodySize = maxBytes;
    }

    // shortcut: the body is not kept, so it can be read once; buffer it to a temporary file when a plugin has to read an attachment before it is stored.
    public override Task<Stream> OpenReadAsync(CancellationToken ct)
    {
        if (_read) throw new InvalidOperationException("An attachment upload can be read once.");
        _read = true;
        return Task.FromResult(request.Body);
    }
}

public static class AttachmentRoutes
{
    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/pages/{id:guid}/attachments", (Guid id, HttpContext http, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AddAttachment { PageId = id, File = new AttachmentUpload(http.Request) }, ct)
                .ToHttp(a => Results.Created(a.Url, a))).WithAction<AddAttachment>();

        api.MapGet("/attachments/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadAttachment { AttachmentId = id }, ct).ToHttp()).AllowAnonymous().WithAction<ReadAttachment>();

        api.MapDelete("/attachments/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemoveAttachment { AttachmentId = id }, ct).ToHttp()).WithAction<RemoveAttachment>();

        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/uploads", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadUploadSettings(), ct).ToHttp()).WithAction<ReadUploadSettings>();

        admin.MapPut("/uploads", (UploadSettingsRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveUploadSettings { AttachmentMb = req.AttachmentMb }, ct).ToHttp()).WithAction<SaveUploadSettings>();
    }
}
