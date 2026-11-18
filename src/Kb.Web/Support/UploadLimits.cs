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

/// <summary>
/// The attachment size limit is the app's own setting, changed in Administration. The web server has a separate limit in front of it
/// (IIS request filtering, 30,000,000 bytes unless web.config says otherwise); whichever is smaller wins.
/// </summary>
public static class UploadLimits
{
    public const string AttachmentKey = "Uploads.MaxAttachmentMb";
    public const int DefaultAttachmentMb = 25;
    public const int MaxMb = 4096;
    private const long IisDefault = 30_000_000;

    public static async Task<int> AttachmentMbAsync(KbDbContext db, CancellationToken ct = default) =>
        int.TryParse(await db.GetAsync(AttachmentKey, ct), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb is >= 1 and <= MaxMb ? mb : DefaultAttachmentMb;

    public static async Task<long> AttachmentBytesAsync(KbDbContext db, CancellationToken ct = default) => await AttachmentMbAsync(db, ct) * 1_048_576L;

    /// <summary>For a route that streams an upload to a temporary file: where it stops reading a file. The action still gives the answer.</summary>
    public static Task<long> AttachmentBytesAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return AttachmentBytesAsync(http.RequestServices.GetRequiredService<KbDbContext>(), http.RequestAborted);
    }

    public static readonly Problem SpaceFull = Problem.Invalid("file", "This space has no room left for attachments. An Owner can remove some, or an Admin can raise the limit.");

    /// <summary>True when the space's attachments, with this many bytes more, would pass the limit set in Kb:Storage:SpaceQuotaMb (0: no limit).</summary>
    public static async Task<bool> SpaceFullAsync(KbDbContext db, int quotaMb, Guid spaceId, long adding, CancellationToken ct)
    {
        if (quotaMb <= 0) return false;
        // system: a total over the space's pages, the binned ones included, for the limit only
        var used = await db.Attachments.IgnoreQueryFilters().Where(a => db.Pages.IgnoreQueryFilters().Any(p => p.Id == a.PageId && p.SpaceId == spaceId)).SumAsync(a => (long?)a.Size, ct) ?? 0;
        return used + adding > quotaMb * 1_048_576L;
    }

    /// <summary>The web server's request size limit for this site, or null when the app is not behind IIS (nothing to read).</summary>
    public static long? ServerLimit(IWebHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID"))) return null;
        return ServerLimitFrom(Path.Combine(env.ContentRootPath, "web.config"));
    }

    /// <summary>maxAllowedContentLength from a web.config, or the IIS default when the file doesn't set one.</summary>
    public static long ServerLimitFrom(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath)) return IisDefault;
            var value = System.Xml.Linq.XDocument.Load(webConfigPath).Descendants("requestLimits").Select(e => (string?)e.Attribute("maxAllowedContentLength")).FirstOrDefault(v => v is not null);
            return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : IisDefault;
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { return IisDefault; }
    }
}
