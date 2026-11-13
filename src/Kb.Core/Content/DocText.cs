using System.Globalization;
using System.Text;
using System.Text.Json;
using Kb.Plugins;

namespace Kb.Core.Content;

public sealed record DocCodeBlock(string Language, string Text, string? Heading);

/// <summary>The renderers plugins registered for editor node types of their own. One per node type.</summary>
public sealed class DocRenderers
{
    private static readonly long Quiet = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
    private readonly Dictionary<string, Registered<NodeRenderer>> _byType = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Type, string Part), long> _reported = new();
    private readonly Action<string, Exception>? _failed;

    public static DocRenderers None { get; } = new([]);

    /// <param name="renderers">In load order; the first for a type is used.</param>
    /// <param name="failed">
    /// Told the plugin id and the error when a renderer fails: at most once a minute for each part of each node type,
    /// because how many such nodes a page holds, and how often it is read, is its writer's and its readers' choice.
    /// </param>
    public DocRenderers(IEnumerable<Registered<NodeRenderer>> renderers, Action<string, Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(renderers);
        foreach (var r in renderers) _byType.TryAdd(r.Value.Type, r);
        _failed = failed;
    }

    public string? Plain(JsonElement node) => Run(node, "plain", r => r.Plain, text => text);

    // The lines are read here, inside the same guard: a renderer may answer a sequence that fails while it is read.
    public IReadOnlyList<string>? MarkdownLines(JsonElement node) => Run(node, "markdown", r => r.MarkdownLines, lines =>
    {
        var list = lines.ToList();
        return list.Contains(null!) ? throw new InvalidOperationException("The renderer answered a line that is null.") : (IReadOnlyList<string>)list;
    });

    public IReadOnlyList<DocBlock>? Blocks(JsonElement node) => Run(node, "blocks", r => r.Blocks, blocks =>
        blocks.All(Whole) ? blocks : throw new InvalidOperationException("The renderer answered a block with a null in it."));

    /// <summary>The writers of Word, PDF and CSV files take a block as it is, so one with a hole in it is not passed on.</summary>
    private static bool Whole(DocBlock? block) => block switch
    {
        DocParagraph p => p.Runs is not null && p.Runs.All(r => r?.Text is not null),
        DocCode c => c.Language is not null && c.Text is not null,
        DocTable t => t.Rows is not null && t.Rows.All(row => row is not null && row.All(cell => cell is not null)),
        DocImage i => i.Alt is not null,
        DocRule => true,
        _ => false,
    };

    /// <summary>What the plugin's renderer gives for this node; null when there is none, it gives nothing, or it fails (then the caller uses the placeholder).</summary>
    private TOut? Run<TRaw, TOut>(JsonElement node, string part, Func<NodeRenderer, Func<JsonElement, TRaw>?> pick, Func<TRaw, TOut> read) where TRaw : class where TOut : class
    {
        if (_byType.Count == 0 || node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
        if (!_byType.TryGetValue(t.GetString()!, out var renderer) || pick(renderer.Value) is not { } render) return null;
        try { return render(node) is { } raw ? read(raw) : null; }
        catch (Exception e) // a plugin's renderer may throw anything; the page is still read
        {
            var (now, key) = (Environment.TickCount64, (renderer.Value.Type, part));
            if (!_reported.TryGetValue(key, out var last) || now - last >= Quiet)
            {
                _reported[key] = now;
                _failed?.Invoke(renderer.PluginId, e);
            }
            return null;
        }
    }
}

/// <summary>
/// Reads an editor document (ProseMirror JSON) as text. One walker feeds search, diff, exports and the agent API,
/// so they all agree on what a page says.
/// </summary>
public static class DocText
{
    private const string ImagePrefix = "/api/v1/images/";

    /// <summary>The node types the editor has by itself. Any other type belongs to a plugin, loaded or not.</summary>
    public static IReadOnlySet<string> CoreNodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "doc", "paragraph", "text", "heading", "codeBlock", "blockquote", "bulletList", "orderedList", "listItem", "taskList", "taskItem",
        "table", "tableRow", "tableHeader", "tableCell", "horizontalRule", "hardBreak", "image",
    };

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); } catch (JsonException) { return null; }
    }

    // A page is stored as its writer sent it, so a node can be of any shape. What is not as expected reads as nothing; it never throws.
    private static string Type(JsonElement n) => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";

    private static string TextOf(JsonElement n) => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";

    private static IEnumerable<JsonElement> Kids(JsonElement n)
    {
        if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) yield break;
        foreach (var k in c.EnumerateArray()) yield return k;
    }

    private static string? Attr(JsonElement n, string name) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null }
            : null;

    /// <summary>Every descendant, depth first, document order.</summary>
    private static IEnumerable<JsonElement> Walk(JsonElement n)
    {
        yield return n;
        foreach (var k in Kids(n))
            foreach (var d in Walk(k)) yield return d;
    }

    /// <summary>The plain text under a node; blocks are separated by a space. A plugin's node gives the text its renderer gives, or the text inside it.</summary>
    private static string InnerText(JsonElement n, DocRenderers r)
    {
        var sb = new StringBuilder();
        Text(n, r, sb);
        return sb.ToString().Trim();
    }

    private static void Text(JsonElement n, DocRenderers r, StringBuilder sb)
    {
        var type = Type(n);
        if (type == "text") { sb.Append(TextOf(n)); return; }
        var plugins = IsPlugins(type);
        if ((plugins || type is "hardBreak" or "paragraph" or "tableCell" or "tableHeader" or "listItem" or "taskItem") && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        if (plugins && r.Plain(n) is { } own) { sb.Append(own); return; }
        foreach (var k in Kids(n)) Text(k, r, sb);
    }

    private static bool IsPlugins(string type) => type.Length > 0 && !CoreNodes.Contains(type);

    /// <summary>What stands for a plugin's node nothing renders: its type, then the text inside it. The node is never read as if it were not there.</summary>
    private static string Placeholder(JsonElement n, DocRenderers r)
    {
        var sb = new StringBuilder();
        foreach (var k in Kids(n)) Text(k, r, sb);
        var inside = sb.ToString().Trim();
        return inside.Length > 0 ? $"[{Type(n)}] {inside}" : $"[{Type(n)}]";
    }

    /// <summary>Plain text for search and snippets: one line per block.</summary>
    public static string Plain(string? json, DocRenderers? renderers = null)
    {
        using var doc = Parse(json);
        if (doc is null) return "";
        var sb = new StringBuilder();
        foreach (var block in Kids(doc.RootElement))
        {
            var text = InnerText(block, renderers ?? DocRenderers.None);
            if (text.Length > 0) sb.Append(text).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The page as Markdown lines. Used for diffs and for agents, which read Markdown far more cheaply than editor JSON.</summary>
    public static IReadOnlyList<string> MarkdownLines(string? json, DocRenderers? renderers = null)
    {
        using var doc = Parse(json);
        var lines = new List<string>();
        if (doc is null) return lines;
        foreach (var block in Kids(doc.RootElement))
        {
            if (lines.Count > 0) lines.Add("");
            Block(block, "", lines, renderers ?? DocRenderers.None);
        }
        return lines;
    }

    public static string Markdown(string? json, DocRenderers? renderers = null) => string.Join('\n', MarkdownLines(json, renderers));

    private static void Block(JsonElement n, string indent, List<string> lines, DocRenderers r)
    {
        switch (Type(n))
        {
            case "heading":
                lines.Add(indent + new string('#', int.TryParse(Attr(n, "level"), out var lv) ? Math.Clamp(lv, 1, 6) : 2) + " " + Inline(n, r));
                break;
            case "codeBlock":
                lines.Add(indent + "```" + (Attr(n, "language") ?? ""));
                foreach (var l in RawText(n).Split('\n')) lines.Add(indent + l);
                lines.Add(indent + "```");
                break;
            case "blockquote":
                foreach (var k in Kids(n)) Block(k, indent + "> ", lines, r);
                break;
            case "bulletList":
            case "orderedList":
            case "taskList":
                var i = 1;
                foreach (var item in Kids(n))
                {
                    var marker = Type(n) switch
                    {
                        "orderedList" => $"{i++}. ",
                        "taskList" => Attr(item, "checked") == "true" ? "- [x] " : "- [ ] ",
                        _ => "- ",
                    };
                    var first = true;
                    foreach (var k in Kids(item))
                    {
                        var sub = new List<string>();
                        Block(k, "", sub, r);
                        foreach (var s in sub)
                        {
                            lines.Add(indent + (first ? marker : new string(' ', marker.Length)) + s);
                            first = false;
                        }
                    }
                    if (first) lines.Add(indent + marker.TrimEnd());
                }
                break;
            case "table":
                var header = true;
                foreach (var row in Kids(n))
                {
                    var cells = Kids(row).Select(c => InnerText(c, r).Replace("|", "\\|", StringComparison.Ordinal)).ToList();
                    lines.Add(indent + "| " + string.Join(" | ", cells) + " |");
                    if (header) lines.Add(indent + "|" + string.Concat(cells.Select(_ => " --- |")));
                    header = false;
                }
                break;
            case "horizontalRule":
                lines.Add(indent + "---");
                break;
            case "image":
                lines.Add(indent + $"![{Attr(n, "alt")}]({Attr(n, "src")})");
                break;
            default:
                if (!IsPlugins(Type(n))) lines.Add(indent + Inline(n, r));
                else if (r.MarkdownLines(n) is { } own) lines.AddRange(own.Select(l => indent + l));
                else lines.Add(indent + Placeholder(n, r));
                break;
        }
    }

    private static string RawText(JsonElement n) =>
        string.Concat(Walk(n).Where(d => Type(d) == "text").Select(TextOf));

    private static string Inline(JsonElement n, DocRenderers r)
    {
        var sb = new StringBuilder();
        foreach (var k in Kids(n))
        {
            switch (Type(k))
            {
                case "text":
                    var t = TextOf(k);
                    if (k.TryGetProperty("marks", out var marks) && marks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in marks.EnumerateArray())
                        {
                            t = Type(m) switch
                            {
                                "code" => $"`{t}`",
                                "bold" => $"**{t}**",
                                "italic" => $"*{t}*",
                                "strike" => $"~~{t}~~",
                                "link" => $"[{t}]({Attr(m, "href")})",
                                _ => t,
                            };
                        }
                    }
                    sb.Append(t);
                    break;
                case "hardBreak": sb.Append(' '); break;
                case "image": sb.Append(CultureInfo.InvariantCulture, $"![{Attr(k, "alt")}]({Attr(k, "src")})"); break;
                default:
                    if (!IsPlugins(Type(k))) sb.Append(Inline(k, r));
                    else sb.Append(r.MarkdownLines(k) is { } own ? string.Join(' ', own) : Placeholder(k, r));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The page as a flat list of blocks, in reading order.</summary>
    public static IReadOnlyList<DocBlock> Blocks(string? json, DocRenderers? renderers = null)
    {
        using var doc = Parse(json);
        var list = new List<DocBlock>();
        if (doc is null) return list;
        foreach (var n in Kids(doc.RootElement)) Flatten(n, list, 0, false, null, renderers ?? DocRenderers.None);
        return list;
    }

    private static void Flatten(JsonElement n, List<DocBlock> list, int indent, bool quote, string? marker, DocRenderers r)
    {
        switch (Type(n))
        {
            case "heading":
                list.Add(new DocParagraph(Runs(n, r), int.TryParse(Attr(n, "level"), out var lv) ? Math.Clamp(lv, 1, 6) : 2));
                break;
            case "codeBlock":
                list.Add(new DocCode(Attr(n, "language") ?? "plaintext", RawText(n)));
                break;
            case "blockquote":
                foreach (var k in Kids(n)) Flatten(k, list, indent + 1, true, null, r);
                break;
            case "bulletList":
            case "orderedList":
            case "taskList":
                var i = 1;
                foreach (var item in Kids(n))
                {
                    var m = Type(n) switch { "orderedList" => $"{i++}.", "taskList" => Attr(item, "checked") == "true" ? "☑" : "☐", _ => "•" };
                    var first = true;
                    foreach (var k in Kids(item))
                    {
                        Flatten(k, list, indent + 1, quote, first ? m : null, r);
                        first = false;
                    }
                }
                break;
            case "table":
                var rows = Kids(n).Select(row => (IReadOnlyList<string>)Kids(row).Select(c => InnerText(c, r)).ToList()).ToList();
                list.Add(new DocTable(rows, Kids(n).FirstOrDefault() is var top && Kids(top).Any(c => Type(c) == "tableHeader")));
                break;
            case "horizontalRule":
                list.Add(new DocRule());
                break;
            case "image":
                var src = Attr(n, "src") ?? "";
                Guid? id = src.StartsWith(ImagePrefix, StringComparison.Ordinal) && Guid.TryParse(src[ImagePrefix.Length..], out var g) ? g : null;
                list.Add(new DocImage(id, Attr(n, "alt") ?? "", int.TryParse(Attr(n, "width"), out var w) ? w : null));
                break;
            default:
                if (!IsPlugins(Type(n))) list.Add(new DocParagraph(Runs(n, r), 0, marker, indent, quote));
                else if (r.Blocks(n) is { } own) list.AddRange(own);
                else list.Add(new DocParagraph([new DocRun(Placeholder(n, r))], 0, marker, indent, quote));
                break;
        }
    }

    private static List<DocRun> Runs(JsonElement n, DocRenderers r)
    {
        var runs = new List<DocRun>();
        foreach (var k in Kids(n))
        {
            if (Type(k) == "hardBreak") { runs.Add(new DocRun("\n")); continue; }
            if (IsPlugins(Type(k))) { runs.Add(new DocRun(r.Plain(k) ?? Placeholder(k, r))); continue; } // a plugin's inline node is its text
            if (Type(k) != "text") { runs.AddRange(Runs(k, r)); continue; }
            var marks = k.TryGetProperty("marks", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.EnumerateArray().ToList() : [];
            bool Has(string t) => marks.Any(m => Type(m) == t);
            var link = marks.FirstOrDefault(m => Type(m) == "link");
            runs.Add(new DocRun(TextOf(k), Has("bold"), Has("italic"), Has("underline"), Has("strike"), Has("code"),
                link.ValueKind == JsonValueKind.Object ? Attr(link, "href") : null));
        }
        return runs;
    }

    /// <summary>Each table as rows of cell text, in document order.</summary>
    public static IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> Tables(string? json)
    {
        using var doc = Parse(json);
        if (doc is null) return [];
        return Walk(doc.RootElement).Where(n => Type(n) == "table")
            .Select(t => (IReadOnlyList<IReadOnlyList<string>>)Kids(t).Select(r => (IReadOnlyList<string>)Kids(r).Select(c => InnerText(c, DocRenderers.None)).ToList()).ToList())
            .ToList();
    }

    /// <summary>Each code block with its language and the nearest heading above it.</summary>
    public static IReadOnlyList<DocCodeBlock> CodeBlocks(string? json)
    {
        using var doc = Parse(json);
        var list = new List<DocCodeBlock>();
        if (doc is null) return list;
        string? heading = null;
        foreach (var n in Walk(doc.RootElement))
        {
            if (Type(n) == "heading") heading = InnerText(n, DocRenderers.None);
            else if (Type(n) == "codeBlock") list.Add(new DocCodeBlock(Attr(n, "language") ?? "plaintext", RawText(n), heading));
        }
        return list;
    }

    /// <summary>Ids of the stored images a document shows.</summary>
    public static IReadOnlyList<Guid> ImageIds(string? json)
    {
        using var doc = Parse(json);
        if (doc is null) return [];
        return Walk(doc.RootElement).Where(n => Type(n) == "image").Select(n => Attr(n, "src") ?? "")
            .Where(s => s.StartsWith(ImagePrefix, StringComparison.Ordinal))
            .Select(s => Guid.TryParse(s[ImagePrefix.Length..], out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToList();
    }
}

/// <summary>Line diff (longest common subsequence), the same shape Git shows.</summary>
public static class LineDiff
{
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffLine> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var result = new List<DiffLine>();
        // Unchanged head and tail are free; only the middle needs the table.
        var head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail]) tail++;
        for (var i = 0; i < head; i++) result.Add(new DiffLine(DiffOp.Same, a[i]));

        int n = a.Count - head - tail, m = b.Count - head - tail;
        if ((long)n * m > MaxCells)
        {
            // shortcut: a huge rewrite is shown as remove-all then add-all; upgrade to Myers diff if this ever matters.
            for (var i = 0; i < n; i++) result.Add(new DiffLine(DiffOp.Del, a[head + i]));
            for (var j = 0; j < m; j++) result.Add(new DiffLine(DiffOp.Add, b[head + j]));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[head + i] == b[head + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[head + x] == b[head + y]) { result.Add(new DiffLine(DiffOp.Same, a[head + x])); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new DiffLine(DiffOp.Del, a[head + x++]));
                else result.Add(new DiffLine(DiffOp.Add, b[head + y++]));
            }
            while (x < n) result.Add(new DiffLine(DiffOp.Del, a[head + x++]));
            while (y < m) result.Add(new DiffLine(DiffOp.Add, b[head + y++]));
        }
        for (var i = a.Count - tail; i < a.Count; i++) result.Add(new DiffLine(DiffOp.Same, a[i]));
        return result;
    }
}
