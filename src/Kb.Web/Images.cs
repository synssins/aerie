using System.Net;
using System.Net.Sockets;
using System.Text;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web;

public sealed record ImageDto(Guid Id, string Url);
public sealed record ImportImageRequest(string Url);

/// <summary>Where uploaded bytes live. IIS: a local folder. Azure: Blob Storage (a second implementation, same interface).</summary>
public interface IFileStore
{
    Task SaveAsync(string key, byte[] data, CancellationToken ct);
    Task<Stream?> OpenAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class LocalFileStore(IOptions<KbOptions> o, IWebHostEnvironment env) : IFileStore
{
    private string Root => Path.IsPathRooted(o.Value.Storage.Folder) ? o.Value.Storage.Folder : Path.Combine(env.ContentRootPath, o.Value.Storage.Folder);

    // Keys are generated GUIDs ("N" format), never user input, so they cannot escape the folder.
    private string PathOf(string key) => Path.Combine(Root, key[..2], key);

    public async Task SaveAsync(string key, byte[] data, CancellationToken ct)
    {
        var p = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, data, ct);
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream?>(File.Exists(PathOf(key)) ? File.OpenRead(PathOf(key)) : null);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }
}

/// <summary>Fetching a URL a user supplied: only public internet addresses, so the server can't be aimed at the internal network.</summary>
public static class NetGuard
{
    public const string ClientName = "remote-images";

    public static bool IsPublic(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)   // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                // link-local, cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19));
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var first = ip.GetAddressBytes()[0];
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None) || (first & 0xFE) == 0xFC);
    }

    /// <summary>Checks the address actually connected to, not just the name, so a DNS answer can't switch after the check.</summary>
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false, // shortcut: redirects are not followed; follow them with a re-check per hop if sites need it
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("Address not allowed.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };
}

public static class ImageEndpoints
{
    public const int MaxImageBytes = 20 * 1_048_576;
    private static readonly TimeSpan PendingLife = TimeSpan.FromDays(7);

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

    /// <summary>Marks the images a saved page or draft uses, so cleanup leaves them alone.</summary>
    public static async Task KeepAsync(KbDbContext db, string? contentJson, CancellationToken ct = default)
    {
        var ids = DocText.ImageIds(contentJson);
        if (ids.Count > 0) await db.SpaceImages.Where(i => ids.Contains(i.Id) && i.Pending).ExecuteUpdateAsync(s => s.SetProperty(i => i.Pending, false), ct);
    }

    /// <summary>Deletes uploads that no saved page or draft ever used. Returns how many.</summary>
    public static async Task<int> CleanupAsync(KbDbContext db, IFileStore store, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now - PendingLife;
        var stale = await db.SpaceImages.Where(i => i.Pending && i.CreatedAt < cutoff).ToListAsync(ct);
        foreach (var img in stale)
        {
            await store.DeleteAsync(img.Id.ToString("N"), ct);
            db.SpaceImages.Remove(img);
        }
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private static IResult TooLarge() => Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["20 MB maximum."] });
    private static IResult NotFetched() => Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = ["Image could not be fetched."] });

    /// <summary>Type from the file's own bytes. Raster only: SVG can carry script.</summary>
    internal static string? Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 6 && Encoding.ASCII.GetString(d, 0, 6) is "GIF87a" or "GIF89a") return "image/gif";
        if (d.Length >= 12 && Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "WEBP") return "image/webp";
        return null;
    }
}

/// <summary>Daily: removes image uploads that were never used.</summary>
public sealed class ImageCleanupService(IServiceScopeFactory scopes, TimeProvider time, ILogger<ImageCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var n = await ImageEndpoints.CleanupAsync(scope.ServiceProvider.GetRequiredService<KbDbContext>(), scope.ServiceProvider.GetRequiredService<IFileStore>(), time.GetUtcNow(), stoppingToken);
                if (n > 0) log.LogInformation("Removed {Count} unused image uploads.", n);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Image cleanup failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class AttachmentEndpoints
{
    // shortcut: 25 MB keeps uploads under IIS's default 28.6 MB request limit; raise both together (web.config requestLimits) for bigger files.
    public const int MaxBytes = 25 * 1_048_576;

    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/pages/{id:guid}/attachments", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Kind == PageKind.Page, ct);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId, ct) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (http.Request.ContentLength is > MaxBytes) return TooLarge();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length > MaxBytes) return TooLarge();
            if (ms.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Empty file."] });
            var name = Path.GetFileName(Uri.UnescapeDataString(http.Request.Headers["X-File-Name"].ToString()));
            var a = new Attachment { PageId = id, FileName = string.IsNullOrWhiteSpace(name) ? "file" : name[..Math.Min(name.Length, 255)], Size = ms.Length, CreatedBy = me, CreatedAt = time.GetUtcNow() };
            await store.SaveAsync(a.Id.ToString("N"), ms.ToArray(), ct);
            db.Attachments.Add(a);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AttachmentAdd, me, "attachment", a.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName, ["page"] = id.ToString() }), ct);
            return Results.Created($"/api/v1/attachments/{a.Id}", new AttachmentDto(a.Id, a.FileName, a.Size, $"/api/v1/attachments/{a.Id}", a.CreatedAt));
        });

        // Always a download, never shown inline: an attachment can be any file type.
        api.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IFileStore store, CancellationToken ct) =>
        {
            var a = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
            var page = a is null ? null : await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == a.PageId, ct);
            if (a is null || page is null || await perms.GetRoleAsync(http.User.UserId(), page.SpaceId, ct) is null) return Results.NotFound();
            var s = await store.OpenAsync(a.Id.ToString("N"), ct);
            if (s is null) return Results.NotFound();
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.Stream(s, "application/octet-stream", a.FileName);
        });

        // Removes it from the page. The file itself is kept.
        api.MapDelete("/attachments/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
            var page = a is null ? null : await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == a.PageId, ct);
            if (a is null || page is null || await perms.GetRoleAsync(me, page.SpaceId, ct) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            a.DeletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AttachmentRemove, me, "attachment", id.ToString(), page.SpaceId, new Dictionary<string, string> { ["name"] = a.FileName }), ct);
            return Results.NoContent();
        });
    }

    private static IResult TooLarge() => Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["25 MB maximum."] });
}
