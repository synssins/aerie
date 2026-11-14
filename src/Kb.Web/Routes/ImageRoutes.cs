namespace Kb.Web;

public sealed record ImportImageRequest(string Url);

/// <summary>The request body as an image. Held in memory once it is read, so every reader starts at the first byte.</summary>
internal sealed class ImageUpload(HttpRequest request) : UploadContent
{
    private byte[]? _data;

    public override string? FileName => Path.GetFileName(Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString()));
    public override long? Length => _data?.Length ?? request.ContentLength;

    public override async Task<Stream> OpenReadAsync(CancellationToken ct)
    {
        if (_data is null)
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, ct);
            _data = ms.ToArray();
        }
        return new MemoryStream(_data, writable: false);
    }
}

public static class ImageRoutes
{
    public static void MapImageEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/spaces/{id:guid}/images", (Guid id, HttpContext http, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AddImage { SpaceId = id, File = new ImageUpload(http.Request) }, ct)
                .ToHttp(image => Results.Created(image.Url, image))).WithAction<AddImage>();

        // Copies an image from another site into the space, so a pasted page keeps its pictures.
        api.MapPost("/spaces/{id:guid}/images/import", (Guid id, ImportImageRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ImportImage { SpaceId = id, Url = req.Url }, ct)
                .ToHttp(image => Results.Created(image.Url, image))).WithAction<ImportImage>();

        api.MapGet("/images/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadImage { ImageId = id }, ct).ToHttp()).AllowAnonymous().WithAction<ReadImage>();
    }
}
