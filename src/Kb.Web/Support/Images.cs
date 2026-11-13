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
