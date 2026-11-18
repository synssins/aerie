using System.Text.Json.Nodes;

namespace Kb.Core.Content;

/// <summary>
/// The addresses a page may hold. A link goes to a place on this site or to http, https or mailto; an image comes from this site, from
/// http or https, or is carried in the page as picture data. Anything else (javascript:, vbscript:, a data: page) is blanked when a page
/// is saved and left out when one is written as Markdown. The app's own editor never showed such an address as a link; a plugin screen
/// or an API client that renders the page's JSON or Markdown would.
/// </summary>
public static class DocLinks
{
    private static string? Scheme(string? address)
    {
        // Browsers ignore tabs, line ends and leading control characters in an address; so does this check.
        var s = new string([.. (address ?? "").Where(c => c > ' ' && c != 0x7f)]);
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !char.IsAsciiLetter(s[0]) || !s[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-')) return null;
        return s[..colon].ToLowerInvariant();
    }

    public static bool SafeLink(string? href) => Scheme(href) is null or "http" or "https" or "mailto";

    public static bool SafeImage(string? src) => Scheme(src) switch
    {
        null or "http" or "https" => true,
        "data" => new string([.. (src ?? "").Where(c => c > ' ')]).StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !src!.Contains("svg", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The document with every address it may not hold blanked. The same text back when there is nothing to blank.</summary>
    public static string Clean(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return json; }
        var changed = false;
        void Walk(JsonNode? node)
        {
            if (node is not JsonObject o) return;
            var type = o["type"] is JsonValue t && t.TryGetValue<string>(out var name) ? name : null;
            if (type == "image" && o["attrs"] is JsonObject image && image["src"] is JsonValue src && src.TryGetValue<string>(out var s) && !SafeImage(s)) { image["src"] = ""; changed = true; }
            if (o["marks"] is JsonArray marks)
                foreach (var m in marks.OfType<JsonObject>())
                    if (m["type"] is JsonValue mt && mt.TryGetValue<string>(out var mark) && mark == "link" && m["attrs"] is JsonObject link
                        && link["href"] is JsonValue href && href.TryGetValue<string>(out var h) && !SafeLink(h)) { link["href"] = ""; changed = true; }
            if (o["content"] is JsonArray kids) foreach (var k in kids) Walk(k);
        }
        Walk(root);
        return changed ? root!.ToJsonString() : json;
    }
}
