namespace Kb.Web;

public sealed record RenameProductRequest(string ProductName);

/// <summary>The request body as a logo. Held in memory once it is read, so every reader starts at the first byte.</summary>
internal sealed class BrandingUpload(HttpRequest request) : UploadContent
{
    private byte[]? _data;

    public override string? FileName => null;
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

public static class BrandingRoutes
{
    public static void MapBrandingEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        api.MapGet("/brand", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadBrand(), ct).ToHttp()).AllowAnonymous().WithAction<ReadBrand>();

        api.MapGet("/brand/logo/{variant}", (string variant, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadBrandLogo { Variant = variant }, ct).ToHttp()).AllowAnonymous().WithAction<ReadBrandLogo>();

        // These two answer 200 without a body, as they always did.
        admin.MapPut("/brand", (RenameProductRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateBrand { ProductName = req.ProductName }, ct).ToHttp(_ => Results.Ok())).WithAction<UpdateBrand>();

        admin.MapPut("/brand/logo/{variant}", (string variant, HttpContext http, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SetBrandLogo { Variant = variant, File = new BrandingUpload(http.Request) }, ct).ToHttp(_ => Results.Ok())).WithAction<SetBrandLogo>();

        admin.MapDelete("/brand/logo/{variant}", (string variant, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RemoveBrandLogo { Variant = variant }, ct).ToHttp()).WithAction<RemoveBrandLogo>();
    }
}
