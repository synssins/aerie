using System.Globalization;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web.Actions;

// The two reads are open to everyone, signed in or not: the sign-in page shows the brand.
// The writes are an Admin's: AuthorizeAsync answers 403 to anyone else. An Admin is always a signed-in person, so ctx.Actor.UserId is set in HandleAsync.

internal sealed class ReadBrandHandler(KbDbContext db, IOptions<KbOptions> options) : IActionHandler<ReadBrand, BrandDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadBrand> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<BrandDto>> HandleAsync(ActionContext<ReadBrand> ctx)
    {
        var brand = options.Value.Brand;
        var name = await db.GetAsync(BrandingEndpoints.ProductNameKey, ctx.Cancel) ?? brand.ProductName;
        var logos = await db.BrandAssets.AsNoTracking().Select(a => new { a.Variant, a.UpdatedAt }).ToListAsync(ctx.Cancel);
        string? Url(string v) => logos.FirstOrDefault(l => l.Variant == v) is { } l ? $"/api/v1/brand/logo/{v}?v={l.UpdatedAt.ToUnixTimeSeconds()}" : null;
        return new BrandDto(name, Url("light"), Url("dark"), brand.PrimaryColor, string.IsNullOrWhiteSpace(brand.SignInNote) ? null : brand.SignInNote,
            await ThemeTokens.ReadAsync(db, ctx.Cancel));
    }
}

internal sealed class ReadBrandLogoHandler(KbDbContext db) : IActionHandler<ReadBrandLogo, FileOut>
{
    private static readonly Dictionary<string, string> Headers = new()
    {
        // Opened directly, an SVG could run script on this origin; the sandbox policy prevents that.
        ["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox",
        ["Cache-Control"] = "public, max-age=86400",
    };
    private BrandAsset? _logo;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadBrandLogo> ctx)
    {
        var variant = ctx.Action.Variant;
        return (_logo = await db.BrandAssets.AsNoTracking().FirstOrDefaultAsync(x => x.Variant == variant, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public Task<Outcome<FileOut>> HandleAsync(ActionContext<ReadBrandLogo> ctx)
    {
        var data = _logo!.Data;
        return Task.FromResult<Outcome<FileOut>>(new FileOut
        {
            ContentType = _logo.ContentType, Headers = Headers, Open = _ => Task.FromResult<Stream>(new MemoryStream(data, writable: false)),
        });
    }
}

internal sealed class UpdateBrandHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<UpdateBrand, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<UpdateBrand> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<UpdateBrand> ctx)
    {
        var name = ctx.Action.ProductName?.Trim() ?? "";
        if (name.Length is < 1 or > 60) return Problem.Invalid("productName", "1-60 characters.");
        await db.SetAsync(BrandingEndpoints.ProductNameKey, name, ctx.Actor.UserId, time.GetUtcNow());
        ctx.Audit(AuditActions.BrandUpdate, "brand", "productName", data: new Dictionary<string, string> { ["productName"] = name });
        return new Unit();
    }
}

internal sealed class SetBrandLogoHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SetBrandLogo, Unit>
{
    private byte[] _data = [];

    public async Task<Problem?> AuthorizeAsync(ActionContext<SetBrandLogo> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        if (!BrandingEndpoints.Variants.Contains(ctx.Action.Variant)) return Problem.NotFound;
        // The size is settled here, not in HandleAsync: a file over the limit is refused before a plugin hook could read it.
        if (ctx.Action.File is not { } file) return null; // no file is an empty one: HandleAsync refuses it as not an image
        if (file.Length is > BrandingEndpoints.MaxLogoBytes) return BrandingEndpoints.TooLarge();
        await using var body = await file.OpenReadAsync(ctx.Cancel);
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ctx.Cancel);
        if (ms.Length > BrandingEndpoints.MaxLogoBytes) return BrandingEndpoints.TooLarge();
        _data = ms.ToArray();
        return null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetBrandLogo> ctx)
    {
        var (variant, data) = (ctx.Action.Variant, _data);
        if (BrandingEndpoints.Sniff(data) is not { } type) return Problem.Invalid("file", "PNG, JPEG, WebP or SVG only.");

        var a = await db.BrandAssets.FirstOrDefaultAsync(x => x.Variant == variant, ctx.Cancel);
        if (a is null) db.BrandAssets.Add(a = new BrandAsset { Variant = variant, ContentType = type, Data = data });
        a.ContentType = type;
        a.Data = data;
        a.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.BrandUpdate, "brand", $"logo.{variant}", data: new Dictionary<string, string> { ["type"] = type, ["bytes"] = data.Length.ToString(CultureInfo.InvariantCulture) });
        return new Unit();
    }
}

internal sealed class RemoveBrandLogoHandler(KbDbContext db, IAccess access) : IActionHandler<RemoveBrandLogo, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveBrandLogo> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveBrandLogo> ctx)
    {
        var variant = ctx.Action.Variant;
        var a = await db.BrandAssets.FirstOrDefaultAsync(x => x.Variant == variant, ctx.Cancel);
        if (a is null) return new Unit();
        db.BrandAssets.Remove(a);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.BrandUpdate, "brand", $"logo.{variant}", data: new Dictionary<string, string> { ["removed"] = "true" });
        return new Unit();
    }
}
