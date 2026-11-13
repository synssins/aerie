using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Kb.Plugins;

namespace Kb.Core.Plugins;

public sealed record PackageContent(PluginManifest Manifest, string Sha256);

/// <summary>
/// Reads and unpacks a plugin package: a zip with <c>kb-plugin.json</c> at the top and the plugin's files.
/// A package comes from outside, so every name and size in it is checked before anything is written.
/// </summary>
public static partial class PluginPackageReader
{
    public const long MaxUnpackedBytes = 200 * 1_048_576;
    public const int MaxEntries = 2000;

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    private static Problem NotAPackage => new(400, "Not a plugin package.");

    /// <summary>The main assembly file of a plugin: what its manifest says, or <c>&lt;id&gt;.dll</c>.</summary>
    public static string MainAssembly(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.IsNullOrWhiteSpace(manifest.Assembly) ? manifest.Id + ".dll" : manifest.Assembly;
    }

    /// <summary>Validates a package. Writes nothing.</summary>
    public static Outcome<PackageContent> Read(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return new Problem(400, "Package too large.");
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (!IsSafe(entry.FullName)) return NotAPackage;
                total += entry.Length;
                if (total > MaxUnpackedBytes) return new Problem(400, "Package too large.");
                names.Add(entry.FullName.Replace('\\', '/'));
            }

            var manifestEntry = zip.GetEntry(PluginManifests.FileName);
            if (manifestEntry is null) return NotAPackage;
            using var reader = new StreamReader(manifestEntry.Open());
            var manifest = PluginManifests.Parse(reader.ReadToEnd());
            if (manifest is null || manifest.Id is null || manifest.Version is null) return NotAPackage;
            if (!PluginId.IsValid(manifest.Id)) return new Problem(400, "Plugin id is not valid.");
            // The version becomes a folder name; the assembly and the exports become file names.
            if (!VersionPattern().IsMatch(manifest.Version)) return new Problem(400, "Version is not valid.");
            var main = MainAssembly(manifest);
            if (!IsFileName(main) || manifest.Exports.Any(e => !IsFileName(e))) return NotAPackage;
            if (!names.Contains(main)) return new Problem(400, $"Missing {main}.");
            return new PackageContent(manifest, Convert.ToHexStringLower(SHA256.HashData(package)));
        }
        catch (InvalidDataException) { return NotAPackage; }
    }

    /// <summary>
    /// Unpacks a package into a folder. Call only after <see cref="Read"/> succeeded.
    /// The sizes a zip states can lie, so the bytes are counted again while they are written.
    /// </summary>
    public static void Unpack(byte[] package, string folder)
    {
        ArgumentNullException.ThrowIfNull(package);
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            if (!IsSafe(entry.FullName)) throw new InvalidDataException("Not a plugin package.");
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Not a plugin package.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var file = File.Create(target);
            int n;
            while ((n = source.Read(buffer)) > 0)
            {
                total += n;
                if (total > MaxUnpackedBytes) throw new InvalidDataException("Package too large.");
                file.Write(buffer, 0, n);
            }
        }
    }

    /// <summary>A relative path that stays inside the folder: no "..", no root, no drive letter.</summary>
    private static bool IsSafe(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains(':', StringComparison.Ordinal)) return false;
        var path = name.Replace('\\', '/');
        return !path.StartsWith('/') && !path.Split('/').Contains("..");
    }

    private static bool IsFileName(string name) =>
        name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && name != ".." && name != ".";
}
