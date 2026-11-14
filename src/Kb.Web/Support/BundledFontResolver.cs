using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Kb.Web;

/// <summary>
/// The fonts PDF export draws with, built into the app (Fonts/, SIL Open Font License, see Fonts/OFL.txt): Liberation Sans and
/// Liberation Mono, which take the same space as Arial and Courier New. The same on every host, with no font installed.
/// </summary>
public sealed class BundledFontResolver : IFontResolver
{
    private static readonly ConcurrentDictionary<string, byte[]?> Read = new(StringComparer.Ordinal);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var mono = familyName.Contains("Courier", StringComparison.OrdinalIgnoreCase) || familyName.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("Mono", StringComparison.OrdinalIgnoreCase);
        var style = (bold, italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" };
        return new FontResolverInfo($"{(mono ? "LiberationMono" : "LiberationSans")}-{style}");
    }

    public byte[]? GetFont(string faceName) => Read.GetOrAdd(faceName, face =>
    {
        using var stream = typeof(BundledFontResolver).Assembly.GetManifestResourceStream($"Kb.Fonts.{face}.ttf");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });
}
