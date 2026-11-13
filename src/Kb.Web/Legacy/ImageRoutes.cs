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

public sealed record ImageDto(Guid Id, string Url);
public sealed record ImportImageRequest(string Url);

public static partial class ImageEndpoints
{
    public static void MapImageEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/spaces/{id:guid}/images", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id, ct) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.CreateEdit, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (http.Request.ContentLength is > MaxImageBytes) return TooLarge();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length > MaxImageBytes) return TooLarge();
            var name = Path.GetFileName(Uri.UnescapeDataString(http.Request.Headers["X-File-Name"].ToString()));
            return await StoreAsync(db, store, id, me, ms.ToArray(), name, time.GetUtcNow(), ct);
        });

        // Copies an image from another site into the space, so a pasted page keeps its pictures.
        api.MapPost("/spaces/{id:guid}/images/import", async (Guid id, ImportImageRequest req, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store,
            IHttpClientFactory clients, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id, ct) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.CreateEdit, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = ["An http or https address."] });
            byte[] data;
            try
            {
                using var response = await clients.CreateClient(NetGuard.ClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxImageBytes) return NotFetched();
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    if (ms.Length + n > MaxImageBytes) return NotFetched();
                    ms.Write(buffer, 0, n);
                }
                data = ms.ToArray();
            }
            catch (Exception e) when (e is HttpRequestException or IOException or SocketException or OperationCanceledException) { return NotFetched(); }
            return await StoreAsync(db, store, id, me, data, Path.GetFileName(uri.LocalPath), time.GetUtcNow(), ct);
        });

        api.MapGet("/images/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, CancellationToken ct) =>
        {
            var img = await db.SpaceImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (img is null) return Results.NotFound();
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, img.SpaceId, ct) is null)
            {
                // A page that uses the image may have moved to another space.
                var visible = await perms.VisibleSpaceIdsAsync(me, ct);
                if (!await db.SpaceImageGrants.AnyAsync(g => g.ImageId == id && visible.Contains(g.SpaceId), ct)) return Results.NotFound();
            }
            var s = await store.OpenAsync(img.Id.ToString("N"), ct);
            if (s is null) return Results.NotFound();
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable"; // an image id never changes content
            return Results.Stream(s, img.ContentType);
        });
    }

    private static IResult TooLarge() => Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["20 MB maximum."] });
    private static IResult NotFetched() => Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = ["Image could not be fetched."] });

    private static async Task<IResult> StoreAsync(KbDbContext db, IFileStore store, Guid spaceId, Guid me, byte[] data, string? name, DateTimeOffset now, CancellationToken ct)
    {
        if (Sniff(data) is not { } type)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["PNG, JPEG, GIF or WebP only."] });
        var img = new SpaceImage
        {
            Id = Guid.NewGuid(), SpaceId = spaceId, ContentType = type, Size = data.Length, CreatedBy = me, CreatedAt = now, Pending = true,
            FileName = string.IsNullOrWhiteSpace(name) ? "image" : name[..Math.Min(name.Length, 255)],
        };
        await store.SaveAsync(img.Id.ToString("N"), data, ct);
        db.SpaceImages.Add(img);
        await db.SaveChangesAsync(ct);
        var url = $"/api/v1/images/{img.Id}";
        return Results.Created(url, new ImageDto(img.Id, url));
    }
}
