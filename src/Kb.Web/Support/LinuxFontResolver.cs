using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Kb.Web;

/// <summary>
/// PDF fonts off Windows (the Docker image): the Liberation fonts, which take the same space as Arial and Courier New.
/// The image installs them (fonts-liberation); any other host needs them in one of the folders.
/// </summary>
public sealed class LinuxFontResolver(IReadOnlyList<string> folders) : IFontResolver
{
    public static readonly string[] SystemFolders = ["/usr/share/fonts", "/usr/local/share/fonts"];
    private readonly ConcurrentDictionary<string, byte[]?> _read = new(StringComparer.Ordinal);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var mono = familyName.Contains("Courier", StringComparison.OrdinalIgnoreCase) || familyName.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("Mono", StringComparison.OrdinalIgnoreCase);
        var style = (bold, italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" };
        return new FontResolverInfo($"{(mono ? "LiberationMono" : "LiberationSans")}-{style}");
    }

    public byte[]? GetFont(string faceName) => _read.GetOrAdd(faceName, face =>
    {
        foreach (var folder in folders.Where(Directory.Exists))
            if (Directory.EnumerateFiles(folder, face + ".ttf", SearchOption.AllDirectories).FirstOrDefault() is { } file) return File.ReadAllBytes(file);
        return null;
    });
}
