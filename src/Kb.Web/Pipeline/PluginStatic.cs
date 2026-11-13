using System.Collections.Concurrent;
using Kb.Core.Plugins;

namespace Kb.Web.Pipeline;

/// <summary>
/// The files of a plugin that the browser may fetch: only a loaded plugin's active version, only inside its <c>ui/</c> folder,
/// only the types a screen needs. Everything else of a package (the manifest, the assemblies) is never served.
/// </summary>
internal static class PluginStatic
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript", [".css"] = "text/css", [".json"] = "application/json", [".map"] = "application/json", [".svg"] = "image/svg+xml",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".woff2"] = "font/woff2",
    };

    /// <summary>The file and its content type, or null: answer 404.</summary>
    public static (string File, string ContentType)? Find(PluginRegistry registry, string id, string version, string? path)
    {
        if (registry.Find(id) is not { Loaded: true } plugin || plugin.Manifest.Version != version || registry.FolderOf(id) is not { } folder) return null;
        if (string.IsNullOrEmpty(path) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)) return null;
        var parts = path.Split('/');
        if (parts[0] != "ui" || parts.Any(p => p.Length == 0 || p == "." || p == "..")) return null;
        if (!Types.TryGetValue(Path.GetExtension(path), out var type)) return null;
        var root = Path.GetFullPath(Path.Combine(folder, "ui")) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(folder, path));
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? (file, type) : null;
    }

    /// <summary>
    /// The address of a loaded plugin's script, or null when it has none. The package's hash is in the address,
    /// so the browser may keep the file for good and still gets the new one after an update or a reinstall.
    /// </summary>
    public static string? ScriptOf(PluginRegistry registry, PluginInfo plugin)
    {
        var (id, version) = (plugin.Manifest.Id, plugin.Manifest.Version);
        if (plugin.Manifest.Ui?.Script is not { Length: > 0 } script || !script.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || Find(registry, id, version, script) is null) return null;
        var marker = Path.Combine(registry.FolderOf(id)!, ".sha256");
        var hash = File.Exists(marker) ? File.ReadAllText(marker) : "";
        return $"/plugins/{id}/{version}/{script}?v={hash[..Math.Min(hash.Length, 12)]}";
    }
}

/// <summary>How many script errors one person's browser may report in a minute. Kept in memory: it only holds back noise.</summary>
internal sealed class UiErrorLimit(TimeProvider time)
{
    public const int PerMinute = 10;
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _sent = new();

    public bool Allow(Guid user)
    {
        var (now, sent) = (time.GetUtcNow(), _sent.GetOrAdd(user, _ => new Queue<DateTimeOffset>()));
        lock (sent)
        {
            while (sent.Count > 0 && now - sent.Peek() >= TimeSpan.FromMinutes(1)) sent.Dequeue();
            if (sent.Count >= PerMinute) return false;
            sent.Enqueue(now);
            return true;
        }
    }
}
