using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Kb.Plugins;

namespace Kb.Web.Import;

/// <summary>
/// 0.11.0 (draft). The regions of an import's documents that a tool is for (<see cref="ToolRegions"/>), found by the app from the source
/// alone, with the patterns the tool declares: never by the model, and the plugin never sees the source. They decide whether such a tool
/// is offered, which region a call is about, and the layout checks on its input: a text written over several lines of one enclosure
/// ("vnet-prod" above "10.50.8.0/22") counts as text of the source, and text given together must stand together.
/// </summary>
public static partial class Regions
{
    /// <summary>
    /// A text may be stacked over the lines of the enclosure it sits in, each part starting within <see cref="Slack"/> columns of the part
    /// above it; one that sits in no enclosure over at most <see cref="MaxParts"/> lines.
    /// </summary>
    public const int MaxParts = 4, Slack = 2;

    /// <summary>The caps on a tool's declaration of regions.</summary>
    public const int MaxPatterns = 16, MaxPattern = 300;

    /// <summary>One region: its number among the tool's regions in the documents (0 first), and its lines as written.</summary>
    public sealed record Region(int Number, IReadOnlyList<string> Lines);

    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$")]
    private static partial Regex TableRule();

    // The app's own line above each file of an item's source.
    [GeneratedRegex(@"^=== .* ===$")]
    private static partial Regex FileHeader();

    private sealed record Compiled(Regex[] Starts, Regex[] Joins, Regex[] Weak, Regex[] Alone);

    private static readonly ConcurrentDictionary<ToolRegions, Compiled> Cache = new(ReferenceEqualityComparer.Instance);

    /// <summary>Why a declaration of regions is not used, or null: too many or too long patterns, one that is not a pattern, a bound out of range.</summary>
    public static string? Problem(ToolRegions regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (var (name, list) in new[] { ("starts", regions.Starts), ("joins", regions.Joins), ("weak", regions.Weak), ("alone", regions.Alone) })
        {
            if (list is null) return $"its regions have no {name} list";
            if (list.Count > MaxPatterns) return $"its regions have more than {MaxPatterns} {name} patterns";
            foreach (var p in list)
            {
                if (string.IsNullOrEmpty(p) || p.Length > MaxPattern) return $"a {name} pattern of its regions is empty or longer than {MaxPattern} characters";
                try { _ = new Regex(p, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException) { return $"a {name} pattern of its regions is not a pattern the app can run"; }
            }
        }
        if (regions.Starts.Count == 0) return "its regions have no starts pattern";
        if (regions.MinLines is < 1 or > 20 || regions.MinStarts is < 0 or > 20 || regions.MaxGap is < 0 or > 5) return "a bound of its regions is out of range";
        return null;
    }

    private static Compiled Compile(ToolRegions r) => Cache.GetOrAdd(r, static r =>
    {
        static Regex[] All(IReadOnlyList<string> list) =>
            [.. list.Select(p => new Regex(p, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))];
        return new Compiled(All(r.Starts), All(r.Joins), All(r.Weak), All(r.Alone));
    });

    private static bool Any(Regex[] patterns, string line)
    {
        foreach (var p in patterns)
        {
            try { if (p.IsMatch(line)) return true; }
            catch (RegexMatchTimeoutException) { }
        }
        return false;
    }

    /// <summary>
    /// The regions of <paramref name="regions"/> in the documents, in order: each starts at a line that matches a starts (or alone)
    /// pattern, goes on over lines that match a starts, alone or joins pattern, with at most <see cref="ToolRegions.MaxGap"/> other lines
    /// in a row, and counts when it holds at least <see cref="ToolRegions.MinLines"/> such lines, <see cref="ToolRegions.MinStarts"/> of
    /// them starts that are not weak, or a line that matches an alone pattern. Markdown tables and the app's file headers are left out.
    /// </summary>
    public static IReadOnlyList<Region> Find(string source, ToolRegions regions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(regions);
        var c = Compile(regions);
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        // A Markdown table: a run of rows between bars that holds a rule row. Its rows are not part of a region.
        var skip = new bool[lines.Length];
        for (var i = 0; i < lines.Length;)
        {
            if (FileHeader().IsMatch(lines[i].Trim())) skip[i] = true;
            if (!TableRow().IsMatch(lines[i])) { i++; continue; }
            var j = i;
            while (j < lines.Length && TableRow().IsMatch(lines[j])) j++;
            if (Enumerable.Range(i, j - i).Any(k => TableRule().IsMatch(lines[k])))
                for (var k = i; k < j; k++) skip[k] = true;
            i = j;
        }
        bool Alone(int k) => !skip[k] && Any(c.Alone, lines[k]);
        bool Starts(int k) => !skip[k] && (Any(c.Starts, lines[k]) || Alone(k));
        var found = new List<Region>();
        for (var i = 0; i < lines.Length;)
        {
            if (!Starts(i)) { i++; continue; }
            var (end, count, strong, gap, alone) = (i, 0, 0, 0, false);
            for (var k = i; k < lines.Length; k++)
            {
                if (Starts(k)) { count++; end = k; gap = 0; if (!Any(c.Weak, lines[k])) strong++; alone |= Alone(k); }
                else if (!skip[k] && Any(c.Joins, lines[k])) { count++; end = k; gap = 0; }
                else if (++gap > regions.MaxGap) break;
            }
            if ((count >= regions.MinLines && strong >= regions.MinStarts) || alone) found.Add(new Region(found.Count, lines[i..(end + 1)]));
            i = end + 1;
        }
        return found;
    }

    /// <summary>
    /// The region in which <paramref name="text"/> is written, or null: on one line as it is (case and runs of spaces aside), or stacked:
    /// its words in order over lines that follow each other (one blank or letterless line may come between), each part on one line with
    /// only spaces between its words, and each starting within <see cref="Slack"/> columns of the part above. All parts sit in the same
    /// enclosure (<see cref="Box"/>), between its top rule and its bottom rule, so a text never crosses a rule. Where the first part sits in
    /// no enclosure, the parts may not sit in one either, and are held to 2 to <see cref="MaxParts"/> lines; <paramref name="fallback"/>
    /// then says so.
    /// </summary>
    public static Region? Locate(string text, IReadOnlyList<Region> regions, out bool fallback)
    {
        ArgumentNullException.ThrowIfNull(regions);
        fallback = false;
        var words = Words(text);
        if (words.Length == 0) return null;
        Frame? first = null;
        foreach (var d in regions)
            for (var i = 0; i < d.Lines.Count; i++)
                if (Stack(d.Lines, i, words, 0, null, null, 0, ref fallback, ref first)) return d;
        return null;
    }

    /// <inheritdoc cref="Locate(string, IReadOnlyList{Region}, out bool)"/>
    public static Region? Locate(string text, IReadOnlyList<Region> regions) => Locate(text, regions, out _);

    /// <summary>
    /// Where <paramref name="text"/> is written, as <see cref="Locate(string, IReadOnlyList{Region}, out bool)"/> finds it: each region and
    /// line it starts on, with the enclosure its first part sits in, or null for none.
    /// </summary>
    public static IEnumerable<(Region Region, Frame? Box)> Places(string text, IReadOnlyList<Region> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var words = Words(text);
        if (words.Length == 0) yield break;
        foreach (var d in regions)
            for (var i = 0; i < d.Lines.Count; i++)
            {
                // shortcut: one place for each line it starts on; a text written twice on one line counts once there.
                var (fallback, first) = (false, (Frame?)null);
                if (Stack(d.Lines, i, words, 0, null, null, 0, ref fallback, ref first)) yield return (d, first);
            }
    }

    // The words of a text, case and spaces aside; a word that is only separators or brackets ("/", ",", "[") is not a word.
    private static string[] Words(string? text) =>
        [.. ImportTools.Normalize(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Any(c => c is not ('/' or ',' or ';' or '[' or ']')))];

    /// <summary>An enclosure of a region: the numbers of its top and bottom rule lines, and the columns of its sides.</summary>
    public readonly record struct Frame(int Top, int Bottom, int Left, int Right);

    private static bool Stack(IReadOnlyList<string> lines, int line, string[] words, int from, (int Start, int End)? above, Frame? box,
        int parts, ref bool fallback, ref Frame? first)
    {
        if (from == words.Length) return true;
        if (line >= lines.Count || (box is null && parts == MaxParts) || (box is { } b && line >= b.Bottom)) return false;
        for (var to = words.Length; to > from; to--)
        {
            // Between two words: spaces, the brackets that mark items ("[storage] [KV]" for "storage KV") and the separators a text set on
            // one line may use for the line breaks of a stacked one ("DC01 (phys) / DC02 (vm)").
            var pattern = new Regex(Edge(words[from], true) + string.Join(@"[\s\[\]/,;]+", words[from..to].Select(Regex.Escape)) + Edge(words[to - 1], false),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            foreach (Match m in pattern.Matches(lines[line]))
            {
                if (above is { } a && Math.Abs(m.Index - a.Start) > Slack) continue;
                var here = Box(lines, line, m.Index, m.Index + m.Length);
                // Every part in the enclosure of the first part, or, where the first is in none, no part in one.
                if (parts > 0 && here != box) continue;
                if (to == words.Length) { fallback = parts > 0 && box is null; if (parts == 0) first = here; return true; }
                // The next part: on the next line, or one further when the next holds no letter or digit.
                for (var next = line + 1; next <= line + 2 && next < lines.Count; next++)
                {
                    if (Stack(lines, next, words, to, (m.Index, m.Index + m.Length), here, parts + 1, ref fallback, ref first)) { if (parts == 0) first = here; return true; }
                    if (lines[next].Any(char.IsLetterOrDigit)) break;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The enclosure that columns <paramref name="start"/> to <paramref name="end"/> of a line sit in, or null: the nearest line above whose
    /// rule (dashes, equals, tildes, underscores, pluses or box-drawing ones) runs over those columns, with a side (a bar, a plus or a
    /// box-drawing corner) on this line where that rule starts and where it ends, and the nearest line below whose rule runs over the same
    /// columns.
    /// </summary>
    public static Frame? Box(IReadOnlyList<string> lines, int line, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var top = -1;
        for (var t = line - 1; t >= 0 && top < 0; t--) if (Covers(lines[t], start, end)) top = t;
        if (top < 0) return null;
        var (left, right) = (start, end - 1);
        while (left > 0 && IsRule(lines[top][left - 1])) left--;
        while (right + 1 < lines[top].Length && IsRule(lines[top][right + 1])) right++;
        if (!IsSide(lines[line], left) || !IsSide(lines[line], right)) return null;
        for (var u = line + 1; u < lines.Count; u++) if (Covers(lines[u], start, end)) return new Frame(top, u, left, right);
        return null;
    }

    private static bool Covers(string line, int start, int end) => end <= line.Length && Enumerable.Range(start, end - start).All(c => IsRule(line[c]));

    private static bool IsRule(char c) => c is '-' or '=' or '~' or '_' or '+' or '─' or '━' or '═' || (c is >= '┌' and <= '╋' && c is not ('│' or '┃')) || c is >= '╒' and <= '╬';

    private static bool IsSide(string line, int at) => at < line.Length && line[at] is '|' or '+' or '│' or '┃' or '║' or (>= '┌' and <= '╋') or (>= '╒' and <= '╬');

    // A word that starts or ends with a letter or digit is not found inside a longer word ("vm" is not in "vmware").
    private static string Edge(string word, bool start) =>
        start ? (char.IsLetterOrDigit(word[0]) ? @"(?<![\p{L}\p{N}])" : "") : (char.IsLetterOrDigit(word[^1]) ? @"(?![\p{L}\p{N}])" : "");
}
