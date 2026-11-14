using System.Text.Json.Serialization;

namespace Kb.Plugins.Actions;

/// <summary>The product name, the logos and the colour. Open without sign-in: the sign-in page shows them.</summary>
[Action("brand.read", Read = true)]
public sealed record ReadBrand : IAction<BrandDto>;

/// <summary>The file of a logo. Open without sign-in.</summary>
[Action("brand.logo.read", Read = true)]
public sealed record ReadBrandLogo : IAction<FileOut>
{
    /// <summary>"light" or "dark".</summary>
    public required string Variant { get; init; }
}

/// <summary>Renames the product. 1 to 60 characters.</summary>
[Action("brand.update")]
public sealed record UpdateBrand : IAction<Unit>
{
    public required string ProductName { get; init; }
}

/// <summary>Stores a logo. PNG, JPEG, WebP or SVG, 5 MB at most: a larger file is refused before any hook runs.</summary>
[Action("brand.logo.set")]
public sealed record SetBrandLogo : IAction<Unit>
{
    /// <summary>"light" or "dark".</summary>
    public required string Variant { get; init; }
    /// <summary>Needed. It is not "required" because System.Text.Json refuses a type with a required member under [JsonIgnore].</summary>
    [JsonIgnore] public UploadContent? File { get; init; }
}

[Action("brand.logo.remove")]
public sealed record RemoveBrandLogo : IAction<Unit>
{
    public required string Variant { get; init; }
}

public sealed record BrandDto(string ProductName, string? LogoLightUrl, string? LogoDarkUrl, string PrimaryColor, string? SignInNote = null);
