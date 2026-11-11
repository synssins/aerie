using System.Text;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web;

public sealed record BrandDto(string ProductName, string? LogoLightUrl, string? LogoDarkUrl, string PrimaryColor);
public sealed record RenameProductRequest(string ProductName);

/// <summary>
/// Branding: product name and company logo (light and dark). Admin-managed, stored in the database so every
/// instance serves the same values. Config Kb:Brand supplies the defaults.
/// </summary>
public static partial class BrandingEndpoints
{
    public const string ProductNameKey = "Brand.ProductName";
    private const int MaxLogoBytes = 5 * 1_048_576;
    private static readonly string[] Variants = ["light", "dark"];

    // Logos are images only. SVG is allowed but must be plain drawing markup, and is served sandboxed.
    [GeneratedRegex(@"<script|<foreignObject|javascript:|\son[a-z]+\s*=|<iframe|<embed|<object|xlink:href\s*=\s*[""']https?:|href\s*=\s*[""']https?:", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSvg();

    public static void MapBrandingEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var id = ctx.HttpContext.User.UserId();
            var db = ctx.HttpContext.RequestServices.GetRequiredService<KbDbContext>();
            return await db.Users.AsNoTracking().AnyAsync(u => u.Id == id && u.IsAdmin && u.DeactivatedAt == null) ? await next(ctx) : Results.StatusCode(403);
        });

        api.MapGet("/brand", async (IOptions<KbOptions> o, KbDbContext db) =>
        {
            var name = await db.GetAsync(ProductNameKey) ?? o.Value.Brand.ProductName;
            var logos = await db.BrandAssets.AsNoTracking().Select(a => new { a.Variant, a.UpdatedAt }).ToListAsync();
            string? Url(string v) => logos.FirstOrDefault(l => l.Variant == v) is { } l ? $"/api/v1/brand/logo/{v}?v={l.UpdatedAt.ToUnixTimeSeconds()}" : null;
            return Results.Ok(new BrandDto(name, Url("light"), Url("dark"), o.Value.Brand.PrimaryColor));
        }).AllowAnonymous();

        api.MapGet("/brand/logo/{variant}", async (string variant, KbDbContext db, HttpContext http) =>
        {
            var a = await db.BrandAssets.AsNoTracking().FirstOrDefaultAsync(x => x.Variant == variant);
            if (a is null) return Results.NotFound();
            // Opened directly, an SVG could run script on this origin; the sandbox policy prevents that.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            http.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Bytes(a.Data, a.ContentType);
        }).AllowAnonymous();

        admin.MapPut("/brand", async (RenameProductRequest req, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            var name = req.ProductName?.Trim() ?? "";
            if (name.Length is < 1 or > 60) return Results.ValidationProblem(new Dictionary<string, string[]> { ["productName"] = ["1-60 characters."] });
            await db.SetAsync(ProductNameKey, name, http.User.UserId(), time.GetUtcNow());
            await audit.AppendAsync(new AuditEvent(AuditActions.BrandUpdate, http.User.UserId(), "brand", "productName", Data: new Dictionary<string, string> { ["productName"] = name }));
            return Results.Ok();
        });

        admin.MapPut("/brand/logo/{variant}", async (string variant, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            if (!Variants.Contains(variant)) return Results.NotFound();
            if (http.Request.ContentLength is > MaxLogoBytes) return TooLarge();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms);
            if (ms.Length > MaxLogoBytes) return TooLarge();
            var data = ms.ToArray();
            var type = Sniff(data);
            if (type is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["PNG, JPEG, WebP or SVG only."] });

            var a = await db.BrandAssets.FirstOrDefaultAsync(x => x.Variant == variant);
            if (a is null) db.BrandAssets.Add(a = new BrandAsset { Variant = variant, ContentType = type, Data = data });
            a.ContentType = type;
            a.Data = data;
            a.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.BrandUpdate, http.User.UserId(), "brand", $"logo.{variant}", Data: new Dictionary<string, string> { ["type"] = type, ["bytes"] = data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            return Results.Ok();
        });

        admin.MapDelete("/brand/logo/{variant}", async (string variant, HttpContext http, KbDbContext db, IAuditLog audit) =>
        {
            var a = await db.BrandAssets.FirstOrDefaultAsync(x => x.Variant == variant);
            if (a is null) return Results.NoContent();
            db.BrandAssets.Remove(a);
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.BrandUpdate, http.User.UserId(), "brand", $"logo.{variant}", Data: new Dictionary<string, string> { ["removed"] = "true" }));
            return Results.NoContent();
        });
    }

    private static IResult TooLarge() => Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["5 MB maximum."] });

    /// <summary>Content type from the file's own bytes; the client's Content-Type header is not trusted.</summary>
    private static string? Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 12 && Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "WEBP") return "image/webp";
        string text;
        try { text = new UTF8Encoding(false, true).GetString(d); } catch (DecoderFallbackException) { return null; }
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) && !UnsafeSvg().IsMatch(text) ? "image/svg+xml" : null;
    }
}
