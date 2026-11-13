using System.Globalization;
using System.Text;
using System.Text.Json;
using Kb.Plugins;

namespace Kb.Core.Content;

public sealed record DocCodeBlock(string Language, string Text, string? Heading);

/// <summary>A run of text with its formatting.</summary>
public sealed record DocRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, bool Code = false, string? Href = null);

/// <summary>A page flattened to simple blocks, for exporters that lay pages out themselves (Word, PDF).</summary>
public abstract record DocBlock;
/// <summary>Heading is 0 for body text. Marker is the list bullet or number, if any. Indent counts list and quote nesting.</summary>
public sealed record DocParagraph(IReadOnlyList<DocRun> Runs, int Heading = 0, string? Marker = null, int Indent = 0, bool Quote = false) : DocBlock;
public sealed record DocCode(string Language, string Text) : DocBlock;
public sealed record DocTable(IReadOnlyList<IReadOnlyList<string>> Rows, bool HeaderRow) : DocBlock;
public sealed record DocImage(Guid? ImageId, string Alt, int? Width) : DocBlock;
public sealed record DocRule : DocBlock;

/// <summary>
/// Reads an editor document (ProseMirror JSON) as text. One walker feeds search, diff, exports and the agent API,
/// so they all agree on what a page says.
/// </summary>
public static class DocText
{
    private const string ImagePrefix = "/api/v1/images/";

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); } catch (JsonException) { return null; }
    }

    private static string Type(JsonElement n) => n.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    private static IEnumerable<JsonElement> Kids(JsonElement n)
    {
        if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) yield break;
        foreach (var k in c.EnumerateArray()) yield return k;
    }

    private static string? Attr(JsonElement n, string name) =>
        n.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null }
            : null;

    /// <summary>Every descendant, depth first, document order.</summary>
    private static IEnumerable<JsonElement> Walk(JsonElement n)
    {
        yield return n;
        foreach (var k in Kids(n))
            foreach (var d in Walk(k)) yield return d;
    }

    /// <summary>The plain text under a node; blocks are separated by a space.</summary>
    private static string InnerText(JsonElement n)
    {
        var sb = new StringBuilder();
        foreach (var d in Walk(n))
        {
            if (Type(d) == "text" && d.TryGetProperty("text", out var t)) sb.Append(t.GetString());
            else if (Type(d) is "hardBreak" or "paragraph" or "tableCell" or "tableHeader" or "listItem" or "taskItem" && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Plain text for search and snippets: one line per block.</summary>
    public static string Plain(string? json)
    {
        using var doc = Parse(json);
        if (doc is null) return "";
        var sb = new StringBuilder();
        foreach (var block in Kids(doc.RootElement))
        {
            var text = InnerText(block);
            if (text.Length > 0) sb.Append(text).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The page as Markdown lines. Used for diffs and for agents, which read Markdown far more cheaply than editor JSON.</summary>
    public static IReadOnlyList<string> MarkdownLines(string? json)
    {
        using var doc = Parse(json);
        var lines = new List<string>();
        if (doc is null) return lines;
        foreach (var block in Kids(doc.RootElement))
        {
            if (lines.Count > 0) lines.Add("");
            Block(block, "", lines);
        }
        return lines;
    }

    public static string Markdown(string? json) => string.Join('\n', MarkdownLines(json));

    private static void Block(JsonElement n, string indent, List<string> lines)
    {
        switch (Type(n))
        {
            case "heading":
                lines.Add(indent + new string('#', int.TryParse(Attr(n, "level"), out var lv) ? Math.Clamp(lv, 1, 6) : 2) + " " + Inline(n));
                break;
            case "codeBlock":
                lines.Add(indent + "```" + (Attr(n, "language") ?? ""));
                foreach (var l in RawText(n).Split('\n')) lines.Add(indent + l);
                lines.Add(indent + "```");
                break;
            case "blockquote":
                foreach (var k in Kids(n)) Block(k, indent + "> ", lines);
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
                        Block(k, "", sub);
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
                    var cells = Kids(row).Select(c => InnerText(c).Replace("|", "\\|", StringComparison.Ordinal)).ToList();
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
                lines.Add(indent + Inline(n));
                break;
        }
    }

    private static string RawText(JsonElement n) =>
        string.Concat(Walk(n).Where(d => Type(d) == "text" && d.TryGetProperty("text", out _)).Select(d => d.GetProperty("text").GetString()));

    private static string Inline(JsonElement n)
    {
        var sb = new StringBuilder();
        foreach (var k in Kids(n))
        {
            switch (Type(k))
            {
                case "text":
                    var t = k.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "";
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
                default: sb.Append(Inline(k)); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The page as a flat list of blocks, in reading order.</summary>
    public static IReadOnlyList<DocBlock> Blocks(string? json)
    {
        using var doc = Parse(json);
        var list = new List<DocBlock>();
        if (doc is null) return list;
        foreach (var n in Kids(doc.RootElement)) Flatten(n, list, 0, false, null);
        return list;
    }

    private static void Flatten(JsonElement n, List<DocBlock> list, int indent, bool quote, string? marker)
    {
        switch (Type(n))
        {
            case "heading":
                list.Add(new DocParagraph(Runs(n), int.TryParse(Attr(n, "level"), out var lv) ? Math.Clamp(lv, 1, 6) : 2));
                break;
            case "codeBlock":
                list.Add(new DocCode(Attr(n, "language") ?? "plaintext", RawText(n)));
                break;
            case "blockquote":
                foreach (var k in Kids(n)) Flatten(k, list, indent + 1, true, null);
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
                        Flatten(k, list, indent + 1, quote, first ? m : null);
                        first = false;
                    }
                }
                break;
            case "table":
                var rows = Kids(n).Select(r => (IReadOnlyList<string>)Kids(r).Select(InnerText).ToList()).ToList();
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
                list.Add(new DocParagraph(Runs(n), 0, marker, indent, quote));
                break;
        }
    }

    private static List<DocRun> Runs(JsonElement n)
    {
        var runs = new List<DocRun>();
        foreach (var k in Kids(n))
        {
            if (Type(k) == "hardBreak") { runs.Add(new DocRun("\n")); continue; }
            if (Type(k) != "text") { runs.AddRange(Runs(k)); continue; }
            var marks = k.TryGetProperty("marks", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.EnumerateArray().ToList() : [];
            bool Has(string t) => marks.Any(m => Type(m) == t);
            var link = marks.FirstOrDefault(m => Type(m) == "link");
            runs.Add(new DocRun(k.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "", Has("bold"), Has("italic"), Has("underline"), Has("strike"), Has("code"),
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
            .Select(t => (IReadOnlyList<IReadOnlyList<string>>)Kids(t).Select(r => (IReadOnlyList<string>)Kids(r).Select(InnerText).ToList()).ToList())
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
            if (Type(n) == "heading") heading = InnerText(n);
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
