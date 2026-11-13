using System.Text;
using System.Text.RegularExpressions;

namespace Kb.Web;

/// <summary>
/// Branding: product name and company logo (light and dark). Admin-managed, stored in the database so every
/// instance serves the same values. Config Kb:Brand supplies the defaults.
/// </summary>
public static partial class BrandingEndpoints
{
    public const string ProductNameKey = "Brand.ProductName";
    internal const int MaxLogoBytes = 5 * 1_048_576;
    internal static readonly string[] Variants = ["light", "dark"];

    // Logos are images only. SVG is allowed but must be plain drawing markup, and is served sandboxed.
    [GeneratedRegex(@"<script|<foreignObject|javascript:|\son[a-z]+\s*=|<iframe|<embed|<object|xlink:href\s*=\s*[""']https?:|href\s*=\s*[""']https?:", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSvg();

    internal static Problem TooLarge() => Problem.Invalid("file", "5 MB maximum.");

    /// <summary>Content type from the file's own bytes; the client's Content-Type header is not trusted.</summary>
    internal static string? Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 12 && Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "WEBP") return "image/webp";
        string text;
        try { text = new UTF8Encoding(false, true).GetString(d); } catch (DecoderFallbackException) { return null; }
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) && !UnsafeSvg().IsMatch(text) ? "image/svg+xml" : null;
    }
}
