using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kb.Core.Ai;
using Kb.Core.Content;

namespace Kb.Web;

/// <summary>
/// A page's content is untrusted input to the AI as much as pasted or imported material. This reads every place text can hide in
/// a page (each block's text, line by line and joined; headings; code; table cells; HTML comments typed as text; an image's alt
/// text and title; a link's address and title; any other attribute) and runs the tripwire over it, after the tripwire's
/// normalization (zero-width and direction characters out, entities decoded). A hit says what it found and where.
/// </summary>
public static partial class PageScan
{
    public sealed record Finding(string Pattern, string Where, string Text);

    [GeneratedRegex(@"<!--(.*?)-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>Every instruction-like text in the page, each once.</summary>
    public static List<Finding> Scan(string contentJson)
    {
        var found = new List<Finding>();
        JsonNode? doc;
        try { doc = JsonNode.Parse(contentJson); } catch (System.Text.Json.JsonException) { return found; }
        void Check(string text, string where)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (Match c in Comment().Matches(text)) Check(c.Groups[1].Value, "an HTML comment");
            if (AiGuard.Tripwire(new Dictionary<string, string> { ["t"] = text }) is { } hit && !found.Any(f => f.Pattern == hit && f.Where == where))
                found.Add(new Finding(hit, where, Snippet(text, hit)));
        }
        void Walk(JsonNode? node, string where)
        {
            if (node is not JsonObject o) return;
            var type = o["type"]?.GetValue<string>() ?? "";
            var here = type switch
            {
                "codeBlock" => "a code block",
                "tableCell" or "tableHeader" => "a table cell",
                "heading" => "a heading",
                "blockquote" => "a quote",
                _ => where,
            };
            if (o["attrs"] is JsonObject attrs)
                foreach (var (k, v) in attrs)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                        Check(s, type == "image" ? k switch { "alt" => "an image's alt text", "title" => "an image's title", "src" => "an image's address", _ => "an image's settings" } : $"the {k} of a {type} block");
            foreach (var m in (o["marks"] as JsonArray ?? []).OfType<JsonObject>())
                if (m["attrs"] is JsonObject ma)
                    foreach (var (k, v) in ma)
                        if (v is JsonValue mv && mv.TryGetValue<string>(out var s))
                            Check(s, m["type"]?.GetValue<string>() == "link" ? (k == "href" ? "a link's address" : "a link's title") : $"the {k} of a mark");
            if (o["text"] is JsonValue t && t.TryGetValue<string>(out var text)) Check(text, here == "" ? "the text" : here);
            var kids = o["content"] as JsonArray;
            if (kids is null) return;
            // A block's words read together, so a phrase split over several pieces of text (bold in the middle) is still one phrase.
            var joined = string.Concat(kids.OfType<JsonObject>().Select(k => k["text"]?.GetValue<string>() ?? (k["type"]?.GetValue<string>() == "hardBreak" ? "\n" : "")));
            if (kids.Count > 1) Check(joined, here == "" ? "the text" : here);
            foreach (var k in kids) Walk(k, here);
        }
        Walk(doc, "");
        return found;
    }

    /// <summary>What a person reads: what was found, and where.</summary>
    public static string Describe(IReadOnlyList<Finding> findings) =>
        string.Join("; ", findings.Take(3).Select(f => $"\"{f.Pattern}\" in {f.Where}")) + (findings.Count > 3 ? $"; and {findings.Count - 3} more" : "");

    /// <summary>A short record of the findings: a page marked reviewed stays clear until something else is found.</summary>
    public static string Hash(IReadOnlyList<Finding> findings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", findings.Select(f => $"{f.Where}|{AiGuard.Normalize(f.Text)}").Order(StringComparer.Ordinal)))))[..32];

    /// <summary>
    /// After a save: the page is flagged (AiFlag says what and where) when it holds instruction-like text that an Owner or Admin
    /// has not marked reviewed. The save itself goes ahead: it is the person's content. True when the flag changed.
    /// </summary>
    public static bool Apply(Page page)
    {
        var before = page.AiFlag;
        if (page.Kind != PageKind.Page) { page.AiFlag = null; return before is not null; }
        var findings = Scan(page.ContentJson);
        page.AiFlag = findings.Count == 0 || page.AiReviewed == Hash(findings) ? null : Describe(findings).Truncate(300);
        return page.AiFlag != before;
    }

    /// <summary>What may be sent to the model: pages not flagged. Every AI feature that reads pages selects through this.</summary>
    public static IQueryable<Page> ForAi(this IQueryable<Page> pages) => pages.Where(p => p.AiFlag == null);

    private static string Snippet(string text, string hit)
    {
        var flat = Regex.Replace(AiGuard.Normalize(text), @"\s+", " ").Trim();
        var at = flat.IndexOf(hit, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at - 30);
        return flat.Substring(start, Math.Min(flat.Length - start, 120));
    }

    private static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
