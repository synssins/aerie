using System.Text.Json;
using System.Text.RegularExpressions;
using Kb.Plugins;

namespace Kb.Core.Plugins;

/// <summary>The versions this host offers to plugins.</summary>
public static class KbHost
{
    public const string Version = "0.2.0";
    /// <summary>Raised one minor step per phase of the plugin model; 1.0.0 when the contracts are frozen.</summary>
    public const string AbstractionsVersion = "0.2.0";
    public const string UiApiVersion = "1.0.0";
}

public static partial class PluginId
{
    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*\z")]
    private static partial Regex Pattern();

    /// <summary>Lowercase letters and digits joined by single dots or hyphens, 3 to 64 characters.</summary>
    public static bool IsValid(string? id) => id is { Length: >= 3 and <= 64 } && Pattern().IsMatch(id);

    /// <summary>
    /// The prefix of a plugin's tables: "kb.email.smtp" gives "plg_kb_email_smtp__". The double underscore ends it,
    /// so the prefix of one plugin is never the start of another's.
    /// </summary>
    public static string TablePrefix(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return "plg_" + id.Replace('.', '_').Replace('-', '_') + "__";
    }
}

public static class PluginManifests
{
    public const string FileName = "kb-plugin.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads a <c>kb-plugin.json</c>. Null when the text is not a manifest.</summary>
    public static PluginManifest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<PluginManifest>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static string Write(PluginManifest manifest) => JsonSerializer.Serialize(manifest, Json);

    /// <summary>The name people see: the manifest's name, or its id.</summary>
    public static string NameOf(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;
    }
}

public static class PluginVersions
{
    /// <summary>
    /// Why a plugin does not fit this host, or null when it fits. Before 1.0.0 a plugin fits when its API version
    /// has the host's major number and is not higher than the host's.
    /// </summary>
    public static string? Refusal(PluginManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (!Version.TryParse(m.Version, out _)) return "Version is not valid.";
        if (!Fits(m.AbstractionsVersion, KbHost.AbstractionsVersion)) return $"Built for a newer plugin API ({m.AbstractionsVersion}).";
        if (!Version.TryParse(m.MinKbVersion, out var min) || min > Version.Parse(KbHost.Version)) return $"Needs version {m.MinKbVersion} or later.";
        if (m.UiApiVersion is not null && !Fits(m.UiApiVersion, KbHost.UiApiVersion)) return $"Built for a newer plugin UI ({m.UiApiVersion}).";
        return null;
    }

    private static bool Fits(string plugin, string host) =>
        Version.TryParse(plugin, out var p) && Version.Parse(host) is var h && p.Major == h.Major && p <= h;

    /// <summary>True when <paramref name="version"/> is at least <paramref name="minimum"/>. A text that is not a version is too low.</summary>
    public static bool AtLeast(string version, string minimum) =>
        Version.TryParse(version, out var v) && Version.TryParse(minimum, out var m) && v >= m;
}

/// <summary>The order to load plugins in, and the ones that cannot load, each with the reason (id to reason).</summary>
public sealed record PluginPlan(IReadOnlyList<string> LoadOrder, IReadOnlyDictionary<string, string> Refused);

public static class PluginGraph
{
    /// <summary>Requirements first; otherwise the order given. A plugin whose requirement is missing, too old, refused or in a circle is refused.</summary>
    public static PluginPlan Order(IReadOnlyCollection<PluginManifest> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, PluginManifest>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in enabled)
        {
            var prefix = PluginId.TablePrefix(m.Id);
            if (prefixes.TryGetValue(prefix, out var first)) refused[m.Id] = $"Conflicts with {first}.";
            else { prefixes[prefix] = m.Id; byId[m.Id] = m; }
        }

        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1: being visited, 2: done
        var path = new List<string>();

        bool Visit(string id)
        {
            if (refused.ContainsKey(id)) return false;
            if (state.TryGetValue(id, out var s))
            {
                if (s == 2) return true;
                foreach (var inCircle in path.Skip(path.IndexOf(id))) refused[inCircle] = "Circular requirement.";
                return false;
            }
            state[id] = 1;
            path.Add(id);
            foreach (var r in byId[id].Requires)
            {
                if (!byId.TryGetValue(r.Id, out var required)) refused.TryAdd(id, $"Requires {r.Id}.");
                else if (!PluginVersions.AtLeast(required.Version, r.MinVersion)) refused.TryAdd(id, $"Requires {r.Id} {r.MinVersion} or later.");
                else if (!Visit(r.Id)) refused.TryAdd(id, $"Requires {PluginManifests.NameOf(required)}.");
            }
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            if (refused.ContainsKey(id)) return false;
            order.Add(id);
            return true;
        }

        foreach (var m in enabled) if (byId.ContainsKey(m.Id) && !refused.ContainsKey(m.Id)) Visit(m.Id);
        return new PluginPlan(order, refused);
    }

    /// <summary>Every plugin that requires <paramref name="id"/>, directly or through another plugin.</summary>
    public static IReadOnlyList<string> Dependents(string id, IReadOnlyCollection<PluginManifest> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var m in enabled)
                if (!seen.Contains(m.Id) && m.Requires.Any(r => seen.Contains(r.Id))) { seen.Add(m.Id); found.Add(m.Id); grew = true; }
        }
        return found;
    }

    /// <summary>
    /// The requirements of <paramref name="m"/> that <paramref name="available"/> does not meet. It takes what is available,
    /// not what was uploaded, so a marketplace can pass its catalogue.
    /// </summary>
    public static IReadOnlyList<PluginRequirement> Missing(PluginManifest m, IReadOnlyCollection<PluginManifest> available)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(available);
        return [.. m.Requires.Where(r => !available.Any(a => a.Id == r.Id && PluginVersions.AtLeast(a.Version, r.MinVersion)))];
    }
}
