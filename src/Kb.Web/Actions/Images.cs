using System.Net.Sockets;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

internal sealed class AddImageHandler(KbDbContext db, IAccess access, IFileStore store, TimeProvider time) : IActionHandler<AddImage, ImageDto>
{
    private byte[]? _data;

    public async Task<Problem?> AuthorizeAsync(ActionContext<AddImage> ctx)
    {
        if (await ImageSupport.MayAddAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel) is { } denied) return denied;
        // The size is settled here, not in HandleAsync: a file over the limit is refused before a plugin hook could read it.
        var file = ctx.Action.File;
        if (file.Length is > ImageEndpoints.MaxImageBytes) return ImageSupport.TooLarge;
        await using var body = await file.OpenReadAsync(ctx.Cancel);
        return (_data = await ImageSupport.ReadAsync(body, ctx.Cancel)) is null ? ImageSupport.TooLarge : null;
    }

    public Task<Outcome<ImageDto>> HandleAsync(ActionContext<AddImage> ctx) =>
        ImageSupport.StoreAsync(db, store, ctx.Action.SpaceId, ctx.Actor.UserId!.Value, _data!, ctx.Action.File.FileName, time.GetUtcNow());
}

internal sealed class ImportImageHandler(KbDbContext db, IAccess access, IFileStore store, IHttpClientFactory clients, TimeProvider time) : IActionHandler<ImportImage, ImageDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ImportImage> ctx) => ImageSupport.MayAddAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel);

    public async Task<Outcome<ImageDto>> HandleAsync(ActionContext<ImportImage> ctx)
    {
        if (!Uri.TryCreate(ctx.Action.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Problem.Invalid("url", "An http or https address.");
        byte[]? data;
        try
        {
            // The NetGuard client connects to public internet addresses only.
            using var response = await clients.CreateClient(NetGuard.ClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ctx.Cancel);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > ImageEndpoints.MaxImageBytes) return ImageSupport.NotFetched;
            await using var body = await response.Content.ReadAsStreamAsync(ctx.Cancel);
            data = await ImageSupport.ReadAsync(body, ctx.Cancel);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or OperationCanceledException) { return ImageSupport.NotFetched; }
        if (data is null) return ImageSupport.NotFetched;
        return await ImageSupport.StoreAsync(db, store, ctx.Action.SpaceId, ctx.Actor.UserId!.Value, data, Path.GetFileName(uri.LocalPath), time.GetUtcNow());
    }
}

internal sealed class ReadImageHandler(KbDbContext db, IAccess access, IFileStore store, IHttpContextAccessor http) : IActionHandler<ReadImage, FileOut>
{
    private static readonly Dictionary<string, string> Headers = new()
    {
        ["Content-Security-Policy"] = "default-src 'none'; sandbox",
        ["Cache-Control"] = "private, max-age=31536000, immutable", // an image id never changes content
    };
    private SpaceImage? _image;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadImage> ctx)
    {
        var id = ctx.Action.ImageId;
        if ((_image = await db.SpaceImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ctx.Cancel)) is null) return Problem.NotFound;
        if (await access.RoleAsync(ctx.Actor, _image.SpaceId, ctx.Cancel) is not null) return null;
        // A page that uses the image may have moved to another space.
        var visible = await access.VisibleSpacesAsync(ctx.Actor, ctx.Cancel);
        return await db.SpaceImageGrants.AnyAsync(g => g.ImageId == id && visible.Contains(g.SpaceId), ctx.Cancel) ? null : Problem.NotFound;
    }

    public async Task<Outcome<FileOut>> HandleAsync(ActionContext<ReadImage> ctx)
    {
        // Opened here, not when the answer is written: a file the store does not have is a 404.
        var file = await store.OpenAsync(_image!.Id.ToString("N"), ctx.Cancel);
        if (file is null) return Problem.NotFound;
        http.HttpContext?.Response.RegisterForDispose(file); // a result filter may still refuse the answer; the file is closed then too
        return new FileOut { ContentType = _image.ContentType, Headers = Headers, Open = _ => Task.FromResult(file) };
    }
}

file static class ImageSupport
{
    public static readonly Problem TooLarge = Problem.Invalid("file", "20 MB maximum.");
    public static readonly Problem NotFetched = Problem.Invalid("url", "Image could not be fetched.");

    public static async Task<Problem?> MayAddAsync(IAccess access, Actor actor, Guid spaceId, CancellationToken ct)
    {
        if (await access.RoleAsync(actor, spaceId, ct) is null) return Problem.NotFound;
        if (!await access.CanAsync(actor, spaceId, SpaceAction.CreateEdit, ct)) return Problem.Forbidden;
        return actor.UserId is null ? Problem.Unauthorized : null; // an image names who added it
    }

    /// <summary>The whole stream, or null when it holds more than an image may.</summary>
    public static async Task<byte[]?> ReadAsync(Stream source, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > ImageEndpoints.MaxImageBytes) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public static async Task<Outcome<ImageDto>> StoreAsync(KbDbContext db, IFileStore store, Guid spaceId, Guid me, byte[] data, string? name, DateTimeOffset now)
    {
        if (ImageEndpoints.Sniff(data) is not { } type) return Problem.Invalid("file", "PNG, JPEG, GIF or WebP only.");
        var img = new SpaceImage
        {
            Id = Guid.NewGuid(), SpaceId = spaceId, ContentType = type, Size = data.Length, CreatedBy = me, CreatedAt = now, Pending = true,
            FileName = string.IsNullOrWhiteSpace(name) ? "image" : name[..Math.Min(name.Length, 255)],
        };
        await store.SaveAsync(img.Id.ToString("N"), data, default);
        db.SpaceImages.Add(img);
        await db.SaveChangesAsync();
        return new ImageDto(img.Id, $"/api/v1/images/{img.Id}");
    }
}
