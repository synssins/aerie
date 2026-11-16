using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kb.Core.Ai;

namespace Kb.Web.Import;

/// <summary>
/// Format by construction. A page is cut into units: each paragraph and heading (each line of one, at a line break), each list
/// item (and each line of an item after a line break), each other block. The model gives each unit a role; the page is rebuilt
/// from its own units, so no word is ever written by the model. It may also call a unit a duplicate: that unit goes only when
/// another unit that stays says the same (after case, spacing, list numbers and end punctuation), and every removal is listed.
/// </summary>
public static partial class DocFormat
{
    public static readonly string[] Roles = ["keep", "paragraph", "heading1", "heading2", "heading3", "step", "bullet", "code", "note", "duplicate"];

    /// <summary>
    /// Kind: "text" (a paragraph or heading, or a line of one), "item" (a list item), "line" (a line of a list item after a
    /// line break; ItemOf is the item's N), "block" (code, a table, a picture, anything else).
    /// </summary>
    public sealed record Unit(int N, string Kind, string Text, JsonObject Node, JsonArray? Content, JsonObject? List, int ItemOf);

    /// <summary>A spelling or grammar correction the model proposes for a unit: a word or short phrase as written, and its correction.</summary>
    public sealed record Fix(string From, string To);

    /// <summary>Removed units in a row: what they said, and what says the same and stays.</summary>
    public sealed record Removal(IReadOnlyList<int> Units, string Text, string SameAs);

    public static List<Unit> Units(JsonObject doc)
    {
        var units = new List<Unit>();
        foreach (var node in (doc["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = node["type"]?.GetValue<string>();
            if (type is "paragraph" or "heading" && IsInline(node))
            {
                foreach (var seg in Segments(node["content"] as JsonArray))
                    units.Add(new Unit(units.Count, "text", PlainOf(seg), node, seg, null, -1));
            }
            else if (type is "orderedList" or "bulletList" or "taskList")
            {
                foreach (var li in (node["content"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var kids = li["content"] as JsonArray;
                    if (kids is { Count: 1 } && kids[0] is JsonObject p && p["type"]?.GetValue<string>() == "paragraph" && IsInline(p) && Segments(p["content"] as JsonArray) is { Count: > 1 } segs)
                    {
                        var item = li.DeepClone().AsObject();
                        item["content"]![0]!["content"] = segs[0].DeepClone();
                        var n = units.Count;
                        units.Add(new Unit(n, "item", PlainOf(segs[0]), item, null, node, -1));
                        foreach (var seg in segs.Skip(1)) units.Add(new Unit(units.Count, "line", PlainOf(seg), node, seg, node, n));
                    }
                    else units.Add(new Unit(units.Count, "item", Plain(li), li, null, node, -1));
                }
            }
            else units.Add(new Unit(units.Count, "block", Plain(node), node, null, null, -1));
        }
        return units;
    }

    /// <summary>What the model reads: one line per unit, with its kind.</summary>
    public static string Lines(IReadOnlyList<Unit> units) => string.Join('\n', units.Select(u =>
        $"[{u.N}] ({u.Kind switch { "text" => u.Node["type"]!.GetValue<string>(), "item" => "list item", "line" => "line of the list item above", _ => u.Node["type"]!.GetValue<string>() }}) {Flat(u.Text)}"));

    /// <summary>
    /// The page rebuilt with the roles. Keep: units the person chose to keep although they were found to be duplicates. Splits: for
    /// a paragraph that runs several items together ("Owner: ... Last verified: ..."), the text each item after the first starts with;
    /// the paragraph is cut there, each item a paragraph of its own. A cut not found at a word start in the text is ignored.
    /// Fixes: spelling and grammar corrections per unit, made in its text only where <see cref="FixError"/> finds none, never in
    /// code (a code block or inline code), quoted text, or a word that looks like an identifier, host name or path.
    /// </summary>
    public static (JsonObject Doc, List<Removal> Removed) Restructure(JsonObject doc, IReadOnlyDictionary<int, string> roles, IReadOnlySet<int>? keep = null,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? splits = null, IReadOnlyDictionary<int, IReadOnlyList<Fix>>? fixes = null)
    {
        var units = Units(doc);
        // A short line after a line break in a list item, with no end punctuation, is a title stuck to the item ("Procedure"),
        // unless the model said what it is.
        string RoleOf(Unit u) => roles.GetValueOrDefault(u.N, "keep");
        string Fallback(Unit u) => u.Kind == "line" && LooksLikeTitle(u.Text) ? "heading2" : "keep";
        var removed = new HashSet<int>();
        var sameAs = new Dictionary<int, int>();
        var marked = units.Where(u => RoleOf(u) == "duplicate" && keep?.Contains(u.N) != true).Select(u => u.N).ToHashSet();
        foreach (var u in units.Where(u => marked.Contains(u.N) && Norm(u.Text).Length > 0))
            if (units.FirstOrDefault(k => k.N != u.N && !removed.Contains(k.N) && Same(k.Text, u.Text)) is { } match)
            {
                removed.Add(u.N);
                sameAs[u.N] = match.N;
            }
        // A title goes too when everything under it went as a duplicate (the title of a repeat). Listed, so it can be kept.
        for (var i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (keep?.Contains(u.N) == true || removed.Contains(u.N) || !IsTitle(u, RoleOf(u) is "duplicate" or "keep" ? Fallback(u) : RoleOf(u))) continue;
            var under = units.Skip(i + 1).TakeWhile(x => !IsTitle(x, RoleOf(x) is "duplicate" or "keep" ? Fallback(x) : RoleOf(x))).ToList();
            if (under.Count > 0 && under.All(x => removed.Contains(x.N))) { removed.Add(u.N); sameAs[u.N] = sameAs[under[0].N]; }
        }
        foreach (var k in sameAs.Keys.ToList()) while (removed.Contains(sameAs[k]) && sameAs.TryGetValue(sameAs[k], out var next) && next != k) sameAs[k] = next;

        var output = new JsonArray();
        JsonObject? list = null, listFrom = null, code = null, lastItem = null;
        string? listRole = null;
        var lastItemN = -1;
        void Close() { list = null; listFrom = null; listRole = null; code = null; lastItem = null; lastItemN = -1; }
        foreach (var u in units)
        {
            if (removed.Contains(u.N)) continue;
            var role = RoleOf(u) is "duplicate" or "keep" ? Fallback(u) : RoleOf(u);
            if (u.Kind == "block") { Close(); output.Add(u.Node.DeepClone()); continue; }
            if (u.Kind == "item" && role is "keep" or "step" or "bullet" or "paragraph")
            {
                // A list item stays a list item, in its own kind of list.
                if (listFrom != u.List)
                {
                    Close();
                    list = Node(u.List!["type"]!.GetValue<string>(), null, u.List["attrs"]?.DeepClone().AsObject());
                    list["content"] = new JsonArray();
                    output.Add(list);
                    listFrom = u.List;
                }
                lastItem = u.Node.DeepClone().AsObject();
                Correct(lastItem, fixes?.GetValueOrDefault(u.N));
                lastItemN = u.N;
                list!["content"]!.AsArray().Add(lastItem);
                continue;
            }
            if (u.Kind == "line" && role == "keep" && lastItemN == u.ItemOf && lastItem?["content"]?[0] is JsonObject para)
            {
                // A line that stays with its item: back after a line break, as it was.
                para["content"]!.AsArray().Add(new JsonObject { ["type"] = "hardBreak" });
                var line = u.Content!.DeepClone().AsArray();
                Correct(line, fixes?.GetValueOrDefault(u.N));
                foreach (var x in line) para["content"]!.AsArray().Add(x!.DeepClone());
                continue;
            }
            var content = u.Content?.DeepClone().AsArray() ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = u.Text });
            Correct(content, fixes?.GetValueOrDefault(u.N));
            if (role is "step" or "bullet")
            {
                code = null; lastItem = null; lastItemN = -1;
                StripListMark(content);
                if (listRole != role || listFrom is not null) { list = Node(role == "step" ? "orderedList" : "bulletList"); list["content"] = new JsonArray(); output.Add(list); listRole = role; listFrom = null; }
                list!["content"]!.AsArray().Add(Node("listItem", new JsonArray(Node("paragraph", content))));
                continue;
            }
            if (role == "code")
            {
                list = null; listFrom = null; listRole = null; lastItem = null; lastItemN = -1;
                if (code is null) { code = Node("codeBlock", null, new JsonObject { ["language"] = "plaintext" }); code["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = u.Text }); output.Add(code); }
                else { var t = code["content"]![0]!; t["text"] = t["text"]!.GetValue<string>() + "\n" + u.Text; }
                continue;
            }
            Close();
            if (u.Kind == "text" && u.Node["type"]?.GetValue<string>() == "paragraph" && role is "paragraph" or "keep" && splits?.GetValueOrDefault(u.N) is { Count: > 0 } at)
            {
                foreach (var piece in Cut(content, at)) output.Add(Node("paragraph", piece));
                continue;
            }
            output.Add(role switch
            {
                "heading1" or "heading2" or "heading3" => Node("heading", content, new JsonObject { ["level"] = role[^1] - '0' }),
                "note" => Node("blockquote", new JsonArray(Node("paragraph", content))),
                "paragraph" => Node("paragraph", content),
                _ => u.Kind == "text" && u.Node["type"]?.GetValue<string>() == "heading" ? Node("heading", content, u.Node["attrs"]?.DeepClone().AsObject()) : Node("paragraph", content),
            });
        }

        var groups = new List<Removal>();
        foreach (var u in units.Where(u => removed.Contains(u.N)))
        {
            if (groups.Count > 0 && groups[^1].Units[^1] == u.N - 1)
                groups[^1] = groups[^1] with { Units = [.. groups[^1].Units, u.N] };
            else groups.Add(new Removal([u.N], "", ""));
        }
        var byN = units.ToDictionary(u => u.N);
        groups = [.. groups.Select(g => g with
        {
            Text = Preview(g.Units.Select(n => byN[n].Text)),
            SameAs = Preview(g.Units.Select(n => sameAs[n]).Distinct().Select(n => byN[n].Text)),
        })];
        return (new JsonObject { ["type"] = "doc", ["content"] = output }, groups);
    }

    /// <summary>The guard behind the construction: every word of the result is a word of the page, in the same order.</summary>
    public static bool OnlyPageWords(string before, string after)
    {
        var have = AiGuard.Words(before);
        var i = 0;
        foreach (var w in AiGuard.Words(after))
        {
            while (i < have.Count && !string.Equals(have[i], w, StringComparison.Ordinal)) i++;
            if (i == have.Count) return false;
            i++;
        }
        return true;
    }

    /// <summary>
    /// Why a proposed fix is not a spelling or grammar correction, or null when it is one: the same numbers, one to three words on
    /// each side and at most one more or fewer, the same first letter, and only a few letters changed (one in four, at least one).
    /// Anything else changes content (a number, another word, the opposite, a rewording) and is refused.
    /// </summary>
    public static string? FixError(Fix fix)
    {
        var (from, to) = (fix.From.Trim(), fix.To.Trim());
        if (from.Length is 0 or > 100 || to.Length is 0 or > 100) return "empty or too long";
        if (from == to) return "no change";
        if (!Digits().Matches(from).Select(m => m.Value).SequenceEqual(Digits().Matches(to).Select(m => m.Value))) return "changes a number";
        var (wf, wt) = (AiGuard.Words(from).Count, AiGuard.Words(to).Count);
        if (wf is < 1 or > 3 || wt is < 1 or > 3 || Math.Abs(wf - wt) > 1) return "changes the words";
        var (a, b) = (from.ToLowerInvariant(), to.ToLowerInvariant());
        if (a == b) return null; // capitals only
        if (a.FirstOrDefault(char.IsLetterOrDigit) != b.FirstOrDefault(char.IsLetterOrDigit)) return "another word";
        return Distance(a, b) <= Math.Max(1, Math.Max(a.Length, b.Length) / 4.0) ? null : "another word";
    }

    // Letters changed between two texts, a swap of two neighbours counting once (optimal string alignment).
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    // Written like an identifier, host name, path, address or version: never corrected.
    [GeneratedRegex(@"[-_./\\@:=#<>`\d]|\p{Ll}\p{Lu}")]
    private static partial Regex IdentifierLike();

    /// <summary>The fixes made in the text nodes under a node (or in an inline content array): not in code, not inside quotes.</summary>
    private static void Correct(JsonNode node, IReadOnlyList<Fix>? fixes)
    {
        if (fixes is not { Count: > 0 }) return;
        var usable = fixes.Where(f => FixError(f) is null && !IdentifierLike().IsMatch(f.From.Trim())).ToList();
        if (usable.Count == 0) return;
        void Walk(JsonNode? n)
        {
            if (n is JsonArray arr) { foreach (var x in arr) Walk(x); return; }
            if (n is not JsonObject o) return;
            if (o["type"]?.GetValue<string>() == "codeBlock") return;
            if (o["text"] is JsonValue t)
            {
                if ((o["marks"] as JsonArray ?? []).Any(m => m?["type"]?.GetValue<string>() == "code")) return;
                var text = t.GetValue<string>();
                foreach (var f in usable) text = Replace(text, f.From.Trim(), f.To.Trim());
                o["text"] = text;
                return;
            }
            Walk(o["content"]);
        }
        Walk(node);
    }

    // Each whole-word use of from, outside double quotes, becomes to.
    private static string Replace(string text, string from, string to)
    {
        static bool Wordy(string s, int i) => i >= 0 && i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '-' or '_' or '/' or '\\' or '@'
            || (s[i] == '.' && i + 1 < s.Length && char.IsLetterOrDigit(s[i + 1])));
        var sb = new System.Text.StringBuilder();
        var at = 0;
        for (var i = text.IndexOf(from, StringComparison.Ordinal); i >= 0; i = text.IndexOf(from, i + from.Length, StringComparison.Ordinal))
        {
            var quoted = text[..i].Count(c => c is '"' or '\u201C' or '\u201D') % 2 == 1;
            if (quoted || Wordy(text, i - 1) || Wordy(text, i + from.Length)) continue;
            sb.Append(text, at, i - at).Append(to);
            at = i + from.Length;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>Inline content cut before each of the texts, in order, where one starts a word; marks stay with their text.</summary>
    private static List<JsonArray> Cut(JsonArray content, IReadOnlyList<string> at)
    {
        var text = PlainOf(content);
        var cuts = new List<int>();
        var from = 1;
        foreach (var s in at.Select(a => a.Trim()).Where(a => a.Length > 0).Take(20))
        {
            for (var i = text.IndexOf(s, from, StringComparison.Ordinal); i > 0; i = text.IndexOf(s, i + 1, StringComparison.Ordinal))
                if (char.IsWhiteSpace(text[i - 1])) { cuts.Add(i); from = i + 1; break; }
        }
        var pieces = new List<JsonArray> { new() };
        var offset = 0;
        foreach (var node in content.OfType<JsonObject>())
        {
            var t = node["text"]?.GetValue<string>() ?? "";
            var start = 0;
            foreach (var c in cuts.Where(c => c > offset && c < offset + t.Length))
            {
                pieces[^1].Add(Piece(node, t[start..(c - offset)]));
                pieces.Add([]);
                start = c - offset;
            }
            pieces[^1].Add(Piece(node, t[start..]));
            offset += t.Length;
            if (cuts.Contains(offset)) pieces.Add([]);
        }
        static string TextOf(JsonNode? n) => n!["text"]!.GetValue<string>();
        foreach (var p in pieces)
        {
            // The spaces between items go with the cut.
            while (p.Count > 0) { var head = TextOf(p[0]).TrimStart(); if (head.Length > 0) { p[0]!["text"] = head; break; } p.RemoveAt(0); }
            while (p.Count > 0) { var tail = TextOf(p[^1]).TrimEnd(); if (tail.Length > 0) { p[^1]!["text"] = tail; break; } p.RemoveAt(p.Count - 1); }
            foreach (var empty in p.Where(x => TextOf(x).Length == 0).ToList()) p.Remove(empty);
        }
        return [.. pieces.Where(p => p.Count > 0)];
    }

    private static JsonObject Piece(JsonObject node, string text)
    {
        var copy = node.DeepClone().AsObject();
        copy["text"] = text;
        return copy;
    }

    private static bool IsTitle(Unit u, string role) =>
        u.Kind is "text" or "line" && (role is "heading1" or "heading2" or "heading3" || (u.Kind == "text" && role is "keep" or "duplicate" && u.Node["type"]?.GetValue<string>() == "heading"));

    private static bool LooksLikeTitle(string text)
    {
        var t = text.Trim();
        return t.Length is > 0 and <= 60 && AiGuard.Words(t).Count <= 5 && !".,;:!?".Contains(t[^1], StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*(?:\d{1,3}[.)]|[-*•])\s+")]
    private static partial Regex ListMark();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>Text as two copies are compared: lower case, one space, no list number in front, no punctuation at the end.</summary>
    public static string Norm(string text) => Space().Replace(ListMark().Replace(text, ""), " ").Trim().TrimEnd('.', ':', ';', ',', '!').Trim().ToLowerInvariant();

    /// <summary>Two copies of one text: the same after Norm, or (long enough to tell) the same words but one in ten.</summary>
    public static bool Same(string a, string b)
    {
        var (x, y) = (Norm(a), Norm(b));
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var (wa, wb) = (AiGuard.Words(x), AiGuard.Words(y));
        if (Math.Min(wa.Count, wb.Count) < 8) return false;
        var common = wa.GroupBy(w => w).Sum(g => Math.Min(g.Count(), wb.Count(w => w == g.Key)));
        return common >= 0.9 * Math.Max(wa.Count, wb.Count);
    }

    private static string Preview(IEnumerable<string> texts)
    {
        var all = string.Join(" / ", texts.Select(Flat));
        return all.Length <= 160 ? all : all[..157].TrimEnd() + "…";
    }

    private static string Flat(string s) => Space().Replace(s, " ").Trim();

    private static bool IsInline(JsonObject node) =>
        node["content"] is JsonArray c && c.Count > 0 && c.All(x => x?["type"]?.GetValue<string>() is "text" or "hardBreak");

    /// <summary>The inline content cut at each line break; empty pieces left out.</summary>
    private static List<JsonArray> Segments(JsonArray? content)
    {
        var parts = new List<JsonArray> { new() };
        foreach (var x in content ?? [])
        {
            if (x?["type"]?.GetValue<string>() == "hardBreak") { parts.Add([]); continue; }
            parts[^1].Add(x!.DeepClone());
        }
        return [.. parts.Where(p => PlainOf(p).Trim().Length > 0)];
    }

    private static string PlainOf(JsonArray content) => string.Concat(content.OfType<JsonObject>().Select(x => x["text"]?.GetValue<string>() ?? ""));

    private static string Plain(JsonNode? node) => node switch
    {
        JsonObject o when o["text"] is JsonValue t => t.GetValue<string>(),
        JsonObject o when o["content"] is JsonArray kids => string.Join(o["type"]?.GetValue<string>() is "paragraph" or "heading" ? "" : " ", kids.Select(Plain)),
        _ => "",
    };

    private static void StripListMark(JsonArray content)
    {
        if (content.FirstOrDefault() is not JsonObject { } first || first["text"] is null) return;
        var text = ListMark().Replace(first["text"]!.GetValue<string>(), "");
        if (text.Length == 0) content.RemoveAt(0); else first["text"] = text;
    }

    private static JsonObject Node(string type, JsonArray? content = null, JsonObject? attrs = null)
    {
        var n = new JsonObject { ["type"] = type };
        if (attrs is not null) n["attrs"] = attrs;
        if (content is { Count: > 0 }) n["content"] = content;
        return n;
    }
}
