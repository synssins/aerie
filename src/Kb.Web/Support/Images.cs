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

public static partial class ImageEndpoints
{
    public const int MaxImageBytes = 20 * 1_048_576;
    private static readonly TimeSpan PendingLife = TimeSpan.FromDays(7);

    /// <summary>
    /// Marks the images a saved page or draft uses, so cleanup leaves them alone. Only images the page's space may show: its own,
    /// and those a move granted it. An upload of another space that a page merely names is not kept alive by that.
    /// </summary>
    public static async Task KeepAsync(KbDbContext db, string? contentJson, Guid spaceId, CancellationToken ct = default)
    {
        var ids = DocText.ImageIds(contentJson);
        if (ids.Count > 0)
            await db.SpaceImages.Where(i => ids.Contains(i.Id) && i.Pending && (i.SpaceId == spaceId || db.SpaceImageGrants.Any(g => g.ImageId == i.Id && g.SpaceId == spaceId)))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Pending, false), ct);
    }

    /// <summary>
    /// A space may show an image of another space while one of its pages uses it (a page moved in). This takes back the grants for
    /// images that no page of the space, live or in its bin, no version and no draft names any more. Returns how many.
    /// </summary>
    public static async Task<int> PruneGrantsAsync(KbDbContext db, Guid spaceId, CancellationToken ct = default)
    {
        var granted = await db.SpaceImageGrants.AsNoTracking().Where(g => g.SpaceId == spaceId).Select(g => g.ImageId).ToListAsync(ct);
        if (granted.Count == 0) return 0;
        // One document in memory at a time: the history of a space can be large, and this runs at every start.
        var used = new HashSet<Guid>();
        var pages = db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == spaceId); // system: pages in the bin can come back, so they count
        await foreach (var json in pages.Select(p => p.ContentJson).AsAsyncEnumerable().WithCancellation(ct)) used.UnionWith(DocText.ImageIds(json));
        await foreach (var json in db.PageVersions.Where(v => pages.Any(p => p.Id == v.PageId)).Select(v => v.ContentJson).AsAsyncEnumerable().WithCancellation(ct)) used.UnionWith(DocText.ImageIds(json));
        await foreach (var json in db.PageDrafts.Where(d => pages.Any(p => p.Id == d.PageId)).Select(d => d.ContentJson).AsAsyncEnumerable().WithCancellation(ct)) used.UnionWith(DocText.ImageIds(json));
        var stale = granted.Where(i => !used.Contains(i)).ToList();
        // Deleted in the database, not through tracked rows: a grant another request took first is no error, and nothing failed stays behind in this request.
        return stale.Count == 0 ? 0 : await db.SpaceImageGrants.Where(g => g.SpaceId == spaceId && stale.Contains(g.ImageId)).ExecuteDeleteAsync(ct);
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
