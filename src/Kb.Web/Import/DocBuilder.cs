using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kb.Web.Import;

/// <summary>
/// One block of a page as the AI proposes it. The model never writes an editor document itself: it returns these typed blocks,
/// and this builds the document, so nothing but the editor's own nodes can come out of it.
/// </summary>
public sealed record AiBlock(string Type, string Text, int Level, IReadOnlyList<string> Items, IReadOnlyList<IReadOnlyList<string>> Rows, string Language, string Image);

public static partial class DocBuilder
{
    /// <summary>The block schema every page proposal uses (import and formatting).</summary>
    public static JsonObject BlockSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["enum"] = new JsonArray("heading", "paragraph", "steps", "bullets", "code", "table", "note", "image") },
            ["text"] = new JsonObject { ["type"] = "string" },
            ["level"] = new JsonObject { ["type"] = "integer" },
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["rows"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
            ["language"] = new JsonObject { ["type"] = "string" },
            ["image"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("type"),
        ["additionalProperties"] = false,
    };

    public static List<AiBlock> ReadBlocks(JsonArray? blocks) =>
        [.. (blocks ?? []).OfType<JsonObject>().Select(b => new AiBlock(
            b["type"]?.GetValue<string>() ?? "paragraph", b["text"]?.GetValue<string>() ?? "", b["level"]?.GetValue<int>() ?? 2,
            [.. (b["items"] as JsonArray ?? []).Select(i => i?.GetValue<string>() ?? "")],
            [.. (b["rows"] as JsonArray ?? []).OfType<JsonArray>().Select(r => (IReadOnlyList<string>)[.. r.Select(c => c?.GetValue<string>() ?? "")])],
            b["language"]?.GetValue<string>() ?? "", b["image"]?.GetValue<string>() ?? ""))];

    /// <summary>
    /// The editor document for the blocks. image(name) gives a picture's address, or null for a picture that is not there (it
    /// is left out). Inline **bold**, *italic* and `code` become marks.
    /// </summary>
    public static JsonObject Build(IEnumerable<AiBlock> blocks, Func<string, string?> image)
    {
        var content = new JsonArray();
        foreach (var b in blocks)
        {
            switch (b.Type)
            {
                case "heading":
                    if (b.Text.Trim().Length > 0) content.Add(Node("heading", Inline(b.Text), new JsonObject { ["level"] = Math.Clamp(b.Level, 1, 3) }));
                    break;
                case "steps" or "bullets":
                    var items = b.Items.Where(i => i.Trim().Length > 0).ToList();
                    if (items.Count == 0 && b.Text.Trim().Length > 0) items = [.. b.Text.Split('\n').Where(l => l.Trim().Length > 0)];
                    if (items.Count == 0) break;
                    // A list's lead-in ("Steps", "Before you start") is text too: kept above the list.
                    if (b.Items.Any(i => i.Trim().Length > 0) && b.Text.Trim().Length > 0) content.Add(Node("paragraph", Inline(b.Text.Trim())));
                    content.Add(Node(b.Type == "steps" ? "orderedList" : "bulletList",
                        new JsonArray([.. items.Select(i => (JsonNode)Node("listItem", new JsonArray(Node("paragraph", Inline(i)))))])));
                    break;
                case "code":
                    content.Add(Node("codeBlock", b.Text.Length > 0 ? new JsonArray(Text(b.Text)) : null, new JsonObject { ["language"] = string.IsNullOrWhiteSpace(b.Language) ? "plaintext" : b.Language.Trim().ToLowerInvariant() }));
                    break;
                case "table":
                    var rows = b.Rows.Where(r => r.Count > 0).ToList();
                    if (rows.Count == 0) break;
                    var width = rows.Max(r => r.Count);
                    content.Add(Node("table", new JsonArray([.. rows.Select((r, i) => (JsonNode)Node("tableRow", new JsonArray([.. Enumerable.Range(0, width)
                        .Select(c => (JsonNode)Node(i == 0 ? "tableHeader" : "tableCell", new JsonArray(Node("paragraph", Inline(c < r.Count ? r[c] : "")))))])))])));
                    break;
                case "note":
                    if (b.Text.Trim().Length > 0) content.Add(Node("blockquote", new JsonArray(Node("paragraph", Inline(b.Text)))));
                    break;
                case "image":
                    if (image(b.Image) is { } src) content.Add(Node("image", null, new JsonObject { ["src"] = src, ["alt"] = b.Text.Length > 0 ? b.Text : b.Image }));
                    break;
                default:
                    foreach (var para in b.Text.Split("\n\n").Where(p => p.Trim().Length > 0)) content.Add(Node("paragraph", Inline(para.Trim())));
                    break;
            }
        }
        return new JsonObject { ["type"] = "doc", ["content"] = content };
    }

    /// <summary>The roles a paragraph can be given by Format.</summary>
    public static readonly string[] Roles = ["paragraph", "heading1", "heading2", "heading3", "step", "bullet", "code", "note"];

    /// <summary>
    /// The top-level paragraphs that have text, in order: what Format shows the model, numbered from 0.
    /// </summary>
    public static List<string> ParagraphLines(JsonObject doc) =>
        [.. (doc["content"] as JsonArray ?? []).OfType<JsonObject>().Where(IsTextParagraph).Select(PlainOf)];

    /// <summary>
    /// Format by construction: each paragraph keeps its own text and marks and only takes the role it was given (a heading, a
    /// step, a bullet, code, a note). A typed list mark ("1.", "-") goes when the line becomes a list item. Everything that is
    /// not a paragraph (pictures, tables, lists already made) stays exactly as it was.
    /// </summary>
    public static JsonObject Restructure(JsonObject doc, IReadOnlyDictionary<int, string> roles)
    {
        var output = new JsonArray();
        JsonObject? list = null;
        string? listRole = null;
        JsonObject? code = null;
        void Close() { list = null; listRole = null; code = null; }
        var n = 0;
        foreach (var node in (doc["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!IsTextParagraph(node)) { Close(); output.Add(node.DeepClone()); continue; }
            var role = roles.GetValueOrDefault(n++, "paragraph");
            var content = node["content"]!.DeepClone().AsArray();
            if (role is "step" or "bullet")
            {
                code = null;
                StripListMark(content);
                if (listRole != role) { list = Node(role == "step" ? "orderedList" : "bulletList", []); list["content"] = new JsonArray(); output.Add(list); listRole = role; }
                list!["content"]!.AsArray().Add(Node("listItem", new JsonArray(Node("paragraph", content))));
                continue;
            }
            if (role == "code")
            {
                list = null; listRole = null;
                if (code is null) { code = Node("codeBlock", null, new JsonObject { ["language"] = "plaintext" }); code["content"] = new JsonArray(Text(PlainOf(node))); output.Add(code); }
                else { var t = code["content"]![0]!; t["text"] = t["text"]!.GetValue<string>() + "\n" + PlainOf(node); }
                continue;
            }
            Close();
            output.Add(role switch
            {
                "heading1" or "heading2" or "heading3" => Node("heading", content, new JsonObject { ["level"] = role[^1] - '0' }),
                "note" => Node("blockquote", new JsonArray(Node("paragraph", content))),
                _ => node.DeepClone(),
            });
        }
        return new JsonObject { ["type"] = "doc", ["content"] = output };
    }

    private static bool IsTextParagraph(JsonObject node) =>
        node["type"]?.GetValue<string>() == "paragraph" && node["content"] is JsonArray { Count: > 0 } c && c.All(x => x?["type"]?.GetValue<string>() is "text" or "hardBreak") && PlainOf(node).Trim().Length > 0;

    private static string PlainOf(JsonObject paragraph) =>
        string.Concat((paragraph["content"] as JsonArray ?? []).OfType<JsonObject>().Select(x => x["type"]?.GetValue<string>() == "hardBreak" ? "\n" : x["text"]?.GetValue<string>() ?? ""));

    private static void StripListMark(JsonArray content)
    {
        if (content.FirstOrDefault() is not JsonObject { } first || first["text"] is null) return;
        var text = ListMark().Replace(first["text"]!.GetValue<string>(), "");
        if (text.Length == 0) content.RemoveAt(0); else first["text"] = text;
    }

    [GeneratedRegex(@"^[ \t]*(?:\d{1,3}[.)]|[-*•])[ \t]+")]
    private static partial Regex ListMark();

    private static JsonObject Node(string type, JsonArray? content = null, JsonObject? attrs = null)
    {
        var n = new JsonObject { ["type"] = type };
        if (attrs is not null) n["attrs"] = attrs;
        if (content is { Count: > 0 }) n["content"] = content;
        return n;
    }

    private static JsonObject Text(string text, params string[] marks)
    {
        var n = new JsonObject { ["type"] = "text", ["text"] = text };
        if (marks.Length > 0) n["marks"] = new JsonArray([.. marks.Select(m => (JsonNode)new JsonObject { ["type"] = m })]);
        return n;
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|(?<![\*\w])\*([^*\s][^*]*?)\*(?![\*\w])")]
    private static partial Regex Marks();

    /// <summary>A line's text with **bold**, *italic* and `code` as marks; line breaks as hard breaks.</summary>
    public static JsonArray Inline(string text)
    {
        var parts = new JsonArray();
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0) parts.Add(new JsonObject { ["type"] = "hardBreak" });
            var line = lines[l];
            var at = 0;
            foreach (Match m in Marks().Matches(line))
            {
                if (m.Index > at) parts.Add(Text(line[at..m.Index]));
                if (m.Groups[1].Success) parts.Add(Text(m.Groups[1].Value, "bold"));
                else if (m.Groups[2].Success) parts.Add(Text(m.Groups[2].Value, "code"));
                else parts.Add(Text(m.Groups[3].Value, "italic"));
                at = m.Index + m.Length;
            }
            if (at < line.Length) parts.Add(Text(line[at..]));
        }
        return parts;
    }

    /// <summary>Every text node of a document, so a redaction or a check can walk them.</summary>
    public static IEnumerable<JsonObject> TextNodes(JsonNode? node)
    {
        if (node is not JsonObject o) yield break;
        if (o["type"]?.GetValue<string>() == "text") yield return o;
        if (o["content"] is JsonArray kids) foreach (var k in kids) foreach (var t in TextNodes(k)) yield return t;
    }
}

/// <summary>A value that looks like a secret or personal data. Sample: masked, for showing; Value: what is redacted.</summary>
public sealed record SensitiveFinding(string Kind, string Sample, string Value);

/// <summary>Finds passwords, keys, tokens and personal data in imported text, and redacts them in a proposal.</summary>
public static partial class Sensitive
{
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|passphrase|secret|api[_ -]?key|access[_ -]?key|client[_ -]?secret|token|pin)\b\s*[:=]\s*[""']?([^\s""',;]{4,})")]
    private static partial Regex Labelled();
    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----[\s\S]+?-----END (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----")]
    private static partial Regex PrivateKey();
    [GeneratedRegex(@"\b(AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|sk_(?:test|live)_[A-Za-z0-9_]{10,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{35}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b")]
    private static partial Regex KnownKey();
    // A credentials list: an account name, then a value with letters and digits or symbols (APP01  administrator  changeme-123).
    [GeneratedRegex(@"(?im)\b(?:administrator|admin|root|sa|svc[_-]\w+|service[_-]?account)\b[ \t|,;]+(?=[^\s|,;]*[A-Za-z])(?=[^\s|,;]*[\d\p{P}\p{S}])([^\s|,;]{6,})")]
    private static partial Regex AccountRow();
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex Ssn();
    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex CardLike();
    [GeneratedRegex(@"(?i)\b(Server|Data Source)=[^;]+;[^\n]*\b(Password|Pwd)=([^;\s]+)")]
    private static partial Regex ConnectionString();

    public static List<SensitiveFinding> Find(string text)
    {
        var found = new List<SensitiveFinding>();
        void Add(string kind, string value) { if (value.Length >= 4 && !found.Any(f => f.Value == value)) found.Add(new SensitiveFinding(kind, Mask(value), value)); }
        foreach (Match m in PrivateKey().Matches(text)) Add("Private key", m.Value);
        foreach (Match m in KnownKey().Matches(text)) Add("Access key or token", m.Value);
        foreach (Match m in ConnectionString().Matches(text)) Add("Password in a connection string", m.Groups[3].Value);
        foreach (Match m in Labelled().Matches(text)) Add($"Value after \"{m.Groups[1].Value}\"", m.Groups[2].Value);
        foreach (Match m in AccountRow().Matches(text)) Add("Password beside an account name", m.Groups[1].Value);
        foreach (Match m in Ssn().Matches(text)) Add("Social security number", m.Value);
        foreach (Match m in CardLike().Matches(text)) { var digits = new string([.. m.Value.Where(char.IsAsciiDigit)]); if (Luhn(digits)) Add("Card number", m.Value.Trim()); }
        return found;
    }

    private static string Mask(string v) => v.Length <= 6 ? new string('•', v.Length) : $"{v[..2]}{new string('•', Math.Min(v.Length - 4, 12))}{v[^2..]}";

    private static bool Luhn(string digits)
    {
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>A copy of the document with every finding's value replaced by [REDACTED].</summary>
    public static JsonObject Redact(JsonObject doc, IReadOnlyList<SensitiveFinding> findings)
    {
        var copy = doc.DeepClone().AsObject();
        if (findings.Count == 0) return copy;
        foreach (var t in DocBuilder.TextNodes(copy))
        {
            var text = t["text"]!.GetValue<string>();
            foreach (var f in findings.OrderByDescending(f => f.Value.Length)) text = text.Replace(f.Value, "[REDACTED]", StringComparison.Ordinal);
            t["text"] = text;
        }
        return copy;
    }
}
