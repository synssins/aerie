using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Kb.Web.DataCenter;

// The storage format (contracts/behaviour/representations.md): an XML fragment with the ac, ri and at prefixes declared. Aerie keeps page
// bodies as editor documents (ProseMirror JSON), so a storage body is converted on the way in and written out again on the way out.
// What maps cleanly to the editor becomes editor nodes; everything else is an opaque node holding its exact markup, written back byte for
// byte in the same place. A mapping is used only when writing it back gives the same XML: that is what makes the round trip safe.

public abstract record SNode(string Raw);
public sealed record SText(string Raw, string Value) : SNode(Raw);
/// <summary>CDATA, comments and processing instructions: kept as written.</summary>
public sealed record SRawOnly(string Raw, string Value) : SNode(Raw);
public sealed record SElem(string Raw, string Name, IReadOnlyList<KeyValuePair<string, string>> Attrs, IReadOnlyList<SNode> Kids) : SNode(Raw)
{
    public string? Attr(string name) => Attrs.FirstOrDefault(a => a.Key == name).Value;
    public IEnumerable<SElem> Elems => Kids.OfType<SElem>();
    /// <summary>The text inside, CDATA included, markup left out.</summary>
    public string Text => string.Concat(Kids.Select(k => k switch { SText t => t.Value, SRawOnly r when r.Raw.StartsWith("<![CDATA[", StringComparison.Ordinal) => r.Value, SElem e => e.Text, _ => "" }));
}

public sealed class StorageException(string message) : Exception(message);

public static class StorageFormat
{
    public const string OpaqueBlock = "dcStorage", OpaqueInline = "dcStorageInline";
    /// <summary>An editor node a plugin added, carried through storage as this macro with the node's JSON as its body.</summary>
    public const string NodeMacro = "aerie-node";
    private static readonly string[] Prefixes = ["ac", "ri", "at", "xml"];

    // ---- parsing: a small XML reader that keeps every node's exact markup ----

    /// <summary>The fragment's nodes. Throws <see cref="StorageException"/> when it is not well-formed.</summary>
    public static IReadOnlyList<SNode> Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var i = 0;
        var nodes = ParseNodes(raw, ref i, null, Prefixes);
        return nodes;
    }

    private static List<SNode> ParseNodes(string s, ref int i, string? closing, IReadOnlyCollection<string> declared)
    {
        var nodes = new List<SNode>();
        while (i < s.Length)
        {
            var start = i;
            if (s[i] != '<')
            {
                var end = s.IndexOf('<', i);
                if (end < 0) end = s.Length;
                var raw = s[i..end];
                nodes.Add(new SText(raw, Decode(raw)));
                i = end;
                continue;
            }
            if (Starts(s, i, "<!--")) { i = Until(s, i, "-->"); nodes.Add(new SRawOnly(s[start..i], "")); continue; }
            if (Starts(s, i, "<![CDATA[")) { i = Until(s, i, "]]>"); nodes.Add(new SRawOnly(s[start..i], s[(start + 9)..(i - 3)])); continue; }
            if (Starts(s, i, "<?")) { i = Until(s, i, "?>"); nodes.Add(new SRawOnly(s[start..i], "")); continue; }
            if (Starts(s, i, "<!")) throw new StorageException("A body may not hold a declaration.");
            if (Starts(s, i, "</"))
            {
                i += 2;
                var name = Name(s, ref i);
                Space(s, ref i);
                if (i >= s.Length || s[i] != '>') throw new StorageException($"The end tag of {name} is not closed.");
                i++;
                if (name != closing) throw new StorageException(closing is null ? $"</{name}> has no start tag." : $"<{closing}> is closed by </{name}>.");
                return nodes;
            }
            i++;
            var tag = Name(s, ref i);
            var attrs = new List<KeyValuePair<string, string>>();
            while (true)
            {
                var had = Space(s, ref i);
                if (i >= s.Length) throw new StorageException($"<{tag}> is not closed.");
                if (s[i] == '>' || Starts(s, i, "/>")) break;
                if (!had) throw new StorageException($"Attributes of <{tag}> need spaces between them.");
                var an = Name(s, ref i);
                Space(s, ref i);
                if (i >= s.Length || s[i] != '=') throw new StorageException($"Attribute {an} of <{tag}> has no value.");
                i++;
                Space(s, ref i);
                if (i >= s.Length || (s[i] != '"' && s[i] != '\'')) throw new StorageException($"Attribute {an} of <{tag}> is not quoted.");
                var q = s[i++];
                var close = s.IndexOf(q, i);
                if (close < 0) throw new StorageException($"Attribute {an} of <{tag}> is not closed.");
                var value = s[i..close];
                if (value.Contains('<', StringComparison.Ordinal)) throw new StorageException($"Attribute {an} of <{tag}> holds a <.");
                if (attrs.Any(a => a.Key == an)) throw new StorageException($"<{tag}> has two {an} attributes.");
                attrs.Add(new(an, Decode(value)));
                i = close + 1;
            }
            // A prefix is declared by the wrapper (ac, ri, at) or by an xmlns: attribute on this element or one above it.
            var scope = declared.Concat(attrs.Where(a => a.Key.StartsWith("xmlns:", StringComparison.Ordinal)).Select(a => a.Key[6..])).ToList();
            foreach (var name in attrs.Select(a => a.Key).Where(a => !a.StartsWith("xmlns", StringComparison.Ordinal)).Prepend(tag)) CheckPrefix(name, scope);
            List<SNode> kids;
            if (s[i] == '/') { i += 2; kids = []; }
            else { i++; kids = ParseNodes(s, ref i, tag, scope); }
            nodes.Add(new SElem(s[start..i], tag, attrs, kids));
        }
        if (closing is not null) throw new StorageException($"<{closing}> is not closed.");
        return nodes;
    }

    private static bool Starts(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

    private static int Until(string s, int i, string end)
    {
        var at = s.IndexOf(end, i, StringComparison.Ordinal);
        if (at < 0) throw new StorageException($"A section opened at {i} is not closed with {end}.");
        return at + end.Length;
    }

    private static bool Space(string s, ref int i)
    {
        var from = i;
        while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n') i++;
        return i > from;
    }

    private static string Name(string s, ref int i)
    {
        var from = i;
        if (i >= s.Length || !(char.IsLetter(s[i]) || s[i] == '_')) throw new StorageException($"A name is expected at {i}.");
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.' or ':')) i++;
        return s[from..i];
    }

    private static void CheckPrefix(string name, IReadOnlyCollection<string> declared)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && !declared.Contains(name[..colon])) throw new StorageException($"The prefix of {name} is not declared.");
    }

    /// <summary>Text with its entities replaced: the five of XML, character references, and the named XHTML ones.</summary>
    private static string Decode(string raw)
    {
        if (!raw.Contains('&', StringComparison.Ordinal)) return raw;
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '&') { sb.Append(raw[i]); continue; }
            var semi = raw.IndexOf(';', i);
            if (semi < 0 || semi - i > 40) throw new StorageException("An & is not part of an entity.");
            var entity = raw[(i + 1)..semi];
            string? text = entity switch
            {
                "lt" => "<", "gt" => ">", "amp" => "&", "quot" => "\"", "apos" => "'",
                _ when entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase) => int.TryParse(entity[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) && h is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(h) : null,
                _ when entity.StartsWith('#') => int.TryParse(entity[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(d) : null,
                _ => WebUtility.HtmlDecode("&" + entity + ";") is var named && named != "&" + entity + ";" ? named : null,
            };
            sb.Append(text ?? throw new StorageException($"&{entity}; is not an entity."));
            i = semi;
        }
        return sb.ToString();
    }

    // ---- storage to the editor document ----

    /// <summary>The editor document for a storage body. Throws <see cref="StorageException"/> for a body aerie refuses.</summary>
    public static JsonObject ToDoc(string storage)
    {
        var nodes = Parse(storage ?? "");
        CheckLayouts(nodes);
        var content = new JsonArray();
        foreach (var n in nodes)
            if (Block(n) is { } b) content.Add(b);
        return new JsonObject { ["type"] = "doc", ["content"] = content };
    }

    private static JsonObject Opaque(SNode n, bool inline) => new() { ["type"] = inline ? OpaqueInline : OpaqueBlock, ["attrs"] = new JsonObject { ["xml"] = n.Raw } };

    private static bool Blank(SNode n) => n is SText t && string.IsNullOrWhiteSpace(t.Value);

    private static readonly string[] Heads = ["h1", "h2", "h3", "h4", "h5", "h6"];

    private static JsonObject? Block(SNode n)
    {
        if (Blank(n)) return null; // whitespace between blocks is not content
        if (n is not SElem e) return Opaque(n, inline: false);
        var bare = e.Attrs.Count == 0;
        switch (e.Name)
        {
            case "p" when bare:
                return Node("paragraph", Inline(e.Kids));
            case var h when bare && Heads.Contains(h):
                return Node("heading", Inline(e.Kids), new JsonObject { ["level"] = h[1] - '0' });
            case "hr" when bare && e.Kids.Count == 0:
                return new JsonObject { ["type"] = "horizontalRule" };
            case "pre" when bare && e.Kids.All(k => k is SText):
                return Code(e.Text, null);
            case "blockquote" when bare && e.Kids.Any(k => !Blank(k)) && e.Kids.All(k => Blank(k) || k is SElem):
                return Node("blockquote", [.. e.Kids.Select(Block).OfType<JsonObject>()]);
            case "ul" or "ol" when bare && List(e) is { } list:
                return list;
            case "table" when bare && Table(e) is { } table:
                return table;
            case "ac:task-list" when bare && Tasks(e) is { } tasks:
                return tasks;
            case "ac:image" when Image(e) is { } image:
                return image;
            case "ac:structured-macro" when CodeMacro(e) is { } code:
                return code;
            case "ac:structured-macro" when PluginNode(e) is { } node:
                return node;
        }
        return Opaque(e, inline: false);
    }

    private static JsonObject Node(string type, JsonArray content, JsonObject? attrs = null)
    {
        var o = new JsonObject { ["type"] = type };
        if (attrs is not null) o["attrs"] = attrs;
        if (content.Count > 0) o["content"] = content;
        return o;
    }

    private static JsonObject Code(string text, string? language)
    {
        var o = new JsonObject { ["type"] = "codeBlock", ["attrs"] = new JsonObject { ["language"] = language } };
        if (text.Length > 0) o["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return o;
    }

    /// <summary>A list whose items hold inline content first, then nested lists only; anything else stays opaque.</summary>
    private static JsonObject? List(SElem e)
    {
        var items = new JsonArray();
        foreach (var k in e.Kids)
        {
            if (Blank(k)) continue;
            if (k is not SElem { Name: "li", Attrs.Count: 0 } li) return null;
            var content = new JsonArray();
            var inline = li.Kids.TakeWhile(x => x is not SElem { Name: "ul" or "ol" }).ToList();
            if (inline.Any(x => x is SElem { Name: "p" or "ul" or "ol" or "table" or "pre" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "hr" })) return null;
            content.Add(Node("paragraph", Inline(inline)));
            foreach (var rest in li.Kids.Skip(inline.Count))
            {
                if (Blank(rest)) continue;
                if (rest is not SElem { Name: "ul" or "ol", Attrs.Count: 0 } nested || List(nested) is not { } sub) return null;
                content.Add(sub);
            }
            items.Add(Node("listItem", content));
        }
        return items.Count == 0 ? null : Node(e.Name == "ul" ? "bulletList" : "orderedList", items);
    }

    /// <summary>table &gt; tbody &gt; tr &gt; th|td, the cells holding block content; anything else stays opaque.</summary>
    private static JsonObject? Table(SElem e)
    {
        var parts = e.Kids.Where(k => !Blank(k)).ToList();
        if (parts is not [SElem { Name: "tbody", Attrs.Count: 0 } body]) return null;
        var rows = new JsonArray();
        foreach (var r in body.Kids)
        {
            if (Blank(r)) continue;
            if (r is not SElem { Name: "tr", Attrs.Count: 0 } tr) return null;
            var cells = new JsonArray();
            foreach (var c in tr.Kids)
            {
                if (Blank(c)) continue;
                if (c is not SElem { Name: "th" or "td" } cell || cell.Attrs.Any(a => a.Key is not ("colspan" or "rowspan") || !int.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 2)) return null;
                if (cell.Kids.Any(k => !Blank(k) && k is not SElem)) return null;
                var blocks = new JsonArray([.. cell.Kids.Select(Block).OfType<JsonObject>()]);
                if (blocks.Count == 0) return null;
                var attrs = new JsonObject
                {
                    ["colspan"] = int.Parse(cell.Attr("colspan") ?? "1", CultureInfo.InvariantCulture),
                    ["rowspan"] = int.Parse(cell.Attr("rowspan") ?? "1", CultureInfo.InvariantCulture),
                };
                cells.Add(Node(cell.Name == "th" ? "tableHeader" : "tableCell", blocks, attrs));
            }
            if (cells.Count == 0) return null;
            rows.Add(Node("tableRow", cells));
        }
        return rows.Count == 0 ? null : Node("table", rows);
    }

    /// <summary>Tasks numbered 1, 2, 3 in order with inline bodies: what aerie writes. Anything else stays opaque.</summary>
    private static JsonObject? Tasks(SElem e)
    {
        var items = new JsonArray();
        var n = 0;
        foreach (var k in e.Kids)
        {
            if (Blank(k)) continue;
            if (k is not SElem { Name: "ac:task", Attrs.Count: 0 } task) return null;
            var parts = task.Kids.Where(x => !Blank(x)).ToList();
            if (parts is not [SElem { Name: "ac:task-id", Attrs.Count: 0 } id, SElem { Name: "ac:task-status", Attrs.Count: 0 } status, SElem { Name: "ac:task-body", Attrs.Count: 0 } body]) return null;
            if (id.Text != (++n).ToString(CultureInfo.InvariantCulture) || status.Text is not ("incomplete" or "complete")) return null;
            if (body.Kids.Any(x => x is SElem { Name: "p" or "ul" or "ol" or "table" })) return null;
            items.Add(Node("taskItem", new JsonArray(Node("paragraph", Inline(body.Kids))), new JsonObject { ["checked"] = status.Text == "complete" }));
        }
        return items.Count == 0 ? null : Node("taskList", items);
    }

    private static JsonObject? Image(SElem e)
    {
        if (e.Attrs.Any(a => a.Key != "ac:alt")) return null;
        var parts = e.Kids.Where(k => !Blank(k)).ToList();
        if (parts is not [SElem { Name: "ri:url", Kids.Count: 0 } url] || url.Attrs.Count != 1 || url.Attr("ri:value") is not { } src) return null;
        return new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["src"] = src, ["alt"] = e.Attr("ac:alt") } };
    }

    /// <summary>The code macro as aerie writes it: only a name, an optional language and a plain-text body.</summary>
    private static JsonObject? CodeMacro(SElem e)
    {
        if (e.Attrs.Count != 1 || e.Attr("ac:name") != "code") return null;
        var parts = e.Kids.Where(k => !Blank(k)).ToList();
        string? language = null;
        if (parts is [SElem { Name: "ac:parameter" } p, ..] && p.Attrs.Count == 1 && p.Attr("ac:name") == "language" && p.Kids.All(k => k is SText))
        {
            language = p.Text;
            parts = parts[1..];
        }
        if (parts is not [SElem { Name: "ac:plain-text-body", Attrs.Count: 0 } body] || !AllCData(body)) return null;
        return language is { Length: 0 } ? null : Code(body.Text, language);
    }

    private static JsonObject? PluginNode(SElem e)
    {
        if (e.Attrs.Count != 1 || e.Attr("ac:name") != NodeMacro) return null;
        var parts = e.Kids.Where(k => !Blank(k)).ToList();
        if (parts is not [SElem { Name: "ac:plain-text-body", Attrs.Count: 0, Kids.Count: > 0 } body] || !AllCData(body)) return null;
        try { return JsonNode.Parse(body.Text) is JsonObject node && node["type"] is JsonValue && Serialize(node) == e.Raw ? node : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>A body of CDATA sections only (a text holding "]]&gt;" is written as two).</summary>
    private static bool AllCData(SElem body) => body.Kids.All(k => k is SRawOnly r && r.Raw.StartsWith("<![CDATA[", StringComparison.Ordinal));

    // Marks in the order aerie writes them, outside first. A nesting in another order stays opaque, so writing it back gives the same XML.
    private static readonly (string Element, string Mark)[] MarkOrder = [("a", "link"), ("strong", "bold"), ("em", "italic"), ("u", "underline"), ("s", "strike"), ("code", "code")];

    private static int Rank(string mark) => Array.FindIndex(MarkOrder, m => m.Mark == mark);

    private static JsonArray Inline(IEnumerable<SNode> kids) => Inline(kids, []);

    private static JsonArray Inline(IEnumerable<SNode> kids, List<JsonObject> marks)
    {
        var result = new JsonArray();
        foreach (var k in kids)
        {
            switch (k)
            {
                case SText t:
                    if (t.Value.Length > 0) result.Add(Text(t.Value, marks));
                    break;
                case SElem { Name: "br", Attrs.Count: 0, Kids.Count: 0 }:
                    result.Add(WithMarks(new JsonObject { ["type"] = "hardBreak" }, marks));
                    break;
                case SElem e when InlineMark(e) is { } mark && (marks.Count == 0 || Rank(mark["type"]!.GetValue<string>()) > Rank(marks[^1]["type"]!.GetValue<string>()))
                                  && e.Kids.Count > 0 && !(mark["type"]!.GetValue<string>() == "code" && e.Kids.Any(x => x is not SText)):
                    foreach (var x in Inline(e.Kids, [.. marks, mark])) result.Add(x?.DeepClone());
                    break;
                default:
                    result.Add(WithMarks(Opaque(k, inline: true), marks));
                    break;
            }
        }
        return result;
    }

    private static JsonObject? InlineMark(SElem e)
    {
        if (e.Name == "a") return e.Attrs.Count == 1 && e.Attr("href") is { } href ? new JsonObject { ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = href } } : null;
        if (e.Attrs.Count != 0) return null;
        return MarkOrder.FirstOrDefault(m => m.Element == e.Name) is { Mark: { } name } ? new JsonObject { ["type"] = name } : null;
    }

    private static JsonObject Text(string text, List<JsonObject> marks) => WithMarks(new JsonObject { ["type"] = "text", ["text"] = text }, marks);

    private static JsonObject WithMarks(JsonObject node, List<JsonObject> marks)
    {
        if (marks.Count > 0) node["marks"] = new JsonArray([.. marks.Select(m => m.DeepClone())]);
        return node;
    }

    /// <summary>A layout is the only top-level element of its body, and each section has as many cells as its type has columns.</summary>
    private static void CheckLayouts(IReadOnlyList<SNode> nodes)
    {
        var layouts = nodes.OfType<SElem>().Where(e => e.Name == "ac:layout").ToList();
        if (layouts.Count == 0) return;
        if (layouts.Count > 1 || nodes.Any(n => !Blank(n) && n is not SElem { Name: "ac:layout" })) throw new StorageException("A body with a layout holds only the layout.");
        foreach (var section in layouts[0].Elems)
        {
            if (section.Name != "ac:layout-section") throw new StorageException("A layout holds only layout sections.");
            var cells = section.Elems.Count(c => c.Name == "ac:layout-cell");
            var wanted = section.Attr("ac:type") switch
            {
                "single" => 1, "two_equal" or "two_left_sidebar" or "two_right_sidebar" => 2, "three_equal" or "three_with_sidebars" => 3,
                var t => throw new StorageException($"{t ?? "(none)"} is not a layout section type."),
            };
            if (cells != wanted) throw new StorageException($"A {section.Attr("ac:type")} section has {wanted} cells.");
        }
    }

    // ---- the editor document to storage ----

    /// <summary>The storage body of an editor document. A document that cannot be read is an empty body.</summary>
    public static string FromDoc(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        JsonNode? doc;
        try { doc = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return ""; }
        var sb = new StringBuilder();
        foreach (var n in Kids(doc)) Write(n, sb);
        return sb.ToString();
    }

    private static string Serialize(JsonObject node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static IEnumerable<JsonObject> Kids(JsonNode? n) => (n?["content"] as JsonArray)?.OfType<JsonObject>() ?? [];
    private static string TypeOf(JsonNode? n) => n?["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";
    private static string? AttrOf(JsonNode? n, string name) => n?["attrs"]?[name] is JsonValue v ? v.ToString() : null;

    private static void Write(JsonObject n, StringBuilder sb)
    {
        switch (TypeOf(n))
        {
            case "paragraph": Wrap(sb, "p", n); break;
            case "heading":
                var level = int.TryParse(AttrOf(n, "level"), NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l is >= 1 and <= 6 ? l : 2;
                Wrap(sb, "h" + level, n);
                break;
            case "horizontalRule": sb.Append("<hr />"); break;
            case "blockquote": sb.Append("<blockquote>"); foreach (var k in Kids(n)) Write(k, sb); sb.Append("</blockquote>"); break;
            case "codeBlock":
                var text = string.Concat(Kids(n).Select(k => k["text"]?.GetValue<string>() ?? ""));
                var language = AttrOf(n, "language");
                if (string.IsNullOrEmpty(language)) sb.Append("<pre>").Append(Escape(text)).Append("</pre>");
                else sb.Append("<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">").Append(Escape(language))
                    .Append("</ac:parameter><ac:plain-text-body>").Append(CData(text)).Append("</ac:plain-text-body></ac:structured-macro>");
                break;
            case "bulletList" or "orderedList":
                var tag = TypeOf(n) == "bulletList" ? "ul" : "ol";
                sb.Append('<').Append(tag).Append('>');
                foreach (var item in Kids(n)) ListItem(item, sb);
                sb.Append("</").Append(tag).Append('>');
                break;
            case "taskList":
                sb.Append("<ac:task-list>");
                var number = 0;
                foreach (var item in Kids(n))
                {
                    sb.Append("<ac:task><ac:task-id>").Append(++number).Append("</ac:task-id><ac:task-status>")
                        .Append(AttrOf(item, "checked") == "true" ? "complete" : "incomplete").Append("</ac:task-status><ac:task-body>");
                    foreach (var p in Kids(item)) { if (TypeOf(p) == "paragraph") Runs(Kids(p), sb); }
                    sb.Append("</ac:task-body></ac:task>");
                }
                sb.Append("</ac:task-list>");
                break;
            case "table":
                sb.Append("<table><tbody>");
                foreach (var row in Kids(n))
                {
                    sb.Append("<tr>");
                    foreach (var cell in Kids(row))
                    {
                        var ct = TypeOf(cell) == "tableHeader" ? "th" : "td";
                        sb.Append('<').Append(ct);
                        foreach (var span in new[] { "colspan", "rowspan" })
                            if (int.TryParse(AttrOf(cell, span), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 1) sb.Append(' ').Append(span).Append("=\"").Append(v).Append('"');
                        sb.Append('>');
                        foreach (var k in Kids(cell)) Write(k, sb);
                        sb.Append("</").Append(ct).Append('>');
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                break;
            case "image":
                sb.Append("<ac:image");
                if (AttrOf(n, "alt") is { Length: > 0 } alt) sb.Append(" ac:alt=\"").Append(EscapeAttr(alt)).Append('"');
                sb.Append("><ri:url ri:value=\"").Append(EscapeAttr(AttrOf(n, "src") ?? "")).Append("\" /></ac:image>");
                break;
            case OpaqueBlock or OpaqueInline: sb.Append(AttrOf(n, "xml") ?? ""); break;
            case "text" or "hardBreak": Runs([n], sb); break;
            default:
                // A node of a plugin: carried as its JSON, so it comes back as it was.
                sb.Append("<ac:structured-macro ac:name=\"").Append(NodeMacro).Append("\"><ac:plain-text-body>")
                    .Append(CData(n.ToJsonString())).Append("</ac:plain-text-body></ac:structured-macro>");
                break;
        }
    }

    private static void Wrap(StringBuilder sb, string tag, JsonObject n)
    {
        sb.Append('<').Append(tag).Append('>');
        Runs(Kids(n), sb);
        sb.Append("</").Append(tag).Append('>');
    }

    /// <summary>The first paragraph of an item is written without its p, the way storage lists hold text; nested lists follow it.</summary>
    private static void ListItem(JsonObject item, StringBuilder sb)
    {
        sb.Append("<li>");
        var first = true;
        foreach (var k in Kids(item))
        {
            if (first && TypeOf(k) == "paragraph") Runs(Kids(k), sb);
            else Write(k, sb);
            first = false;
        }
        sb.Append("</li>");
    }

    /// <summary>Inline nodes with their marks. A mark that goes on over the next node is kept open, so each mark is one element.</summary>
    private static void Runs(IEnumerable<JsonObject> nodes, StringBuilder sb)
    {
        var open = new List<JsonObject>();
        foreach (var n in nodes)
        {
            var marks = ((n["marks"] as JsonArray)?.OfType<JsonObject>() ?? []).Where(m => Rank(TypeOf(m)) >= 0).OrderBy(m => Rank(TypeOf(m))).ToList();
            var keep = 0;
            while (keep < open.Count && keep < marks.Count && JsonNode.DeepEquals(open[keep], marks[keep])) keep++;
            for (var i = open.Count - 1; i >= keep; i--) sb.Append("</").Append(ElementOf(open[i])).Append('>');
            open.RemoveRange(keep, open.Count - keep);
            foreach (var m in marks.Skip(keep))
            {
                sb.Append('<').Append(ElementOf(m));
                if (TypeOf(m) == "link") sb.Append(" href=\"").Append(EscapeAttr(AttrOf(m, "href") ?? "")).Append('"');
                sb.Append('>');
                open.Add(m);
            }
            switch (TypeOf(n))
            {
                case "text": sb.Append(Escape(n["text"]?.GetValue<string>() ?? "")); break;
                case "hardBreak": sb.Append("<br />"); break;
                default: Write(n, sb); break;
            }
        }
        for (var i = open.Count - 1; i >= 0; i--) sb.Append("</").Append(ElementOf(open[i])).Append('>');
    }

    private static string ElementOf(JsonObject mark) => MarkOrder[Rank(TypeOf(mark))].Element;

    public static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    private static string EscapeAttr(string text) => Escape(text).Replace("\"", "&quot;", StringComparison.Ordinal);
    private static string CData(string text) => "<![CDATA[" + text.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal) + "]]>";

    // ---- macros ----

    /// <summary>Every structured macro in a body, outside ones first.</summary>
    public static IEnumerable<SElem> Macros(IEnumerable<SNode> nodes)
    {
        foreach (var e in nodes.OfType<SElem>())
        {
            if (e.Name == "ac:structured-macro") yield return e;
            foreach (var inner in Macros(e.Kids)) yield return inner;
        }
    }

    /// <summary>A macro's name, its body in storage format (or the text of a plain-text body) and its parameters.</summary>
    public static (string Name, string Body, Dictionary<string, string> Parameters) Describe(SElem macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in macro.Elems.Where(e => e.Name == "ac:parameter"))
            parameters[p.Attr("ac:name") ?? ""] = string.Concat(p.Kids.Select(k => k is SText t ? t.Value : k.Raw));
        var rich = macro.Elems.FirstOrDefault(e => e.Name == "ac:rich-text-body");
        var plain = macro.Elems.FirstOrDefault(e => e.Name == "ac:plain-text-body");
        var body = rich is not null ? string.Concat(rich.Kids.Select(k => k.Raw)) : plain?.Text ?? "";
        return (macro.Attr("ac:name") ?? "", body, parameters);
    }
}
