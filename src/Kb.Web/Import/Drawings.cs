using System.Text.RegularExpressions;

namespace Kb.Web.Import;

/// <summary>
/// 0.11.0 (draft). The drawings made of text characters in an import's documents (a network map of boxes and lines drawn with dashes and
/// bars), found by the app from the source alone, never by the model. They decide whether a drawing tool is offered, which drawing a call
/// draws, and let a label written over several lines of one box ("vnet-prod" above "10.50.8.0/22") count as text of the source.
/// </summary>
public static partial class Drawings
{
    /// <summary>
    /// Lines that make a drawing: at least this many drawn lines; inside it, at most this many lines in a row that are not drawn (labels
    /// beside a line, blank lines). A label may be stacked over the lines of the box it sits in, each part starting within
    /// <see cref="Slack"/> columns of the part above it; one that sits in no box over at most <see cref="MaxParts"/> lines.
    /// </summary>
    public const int MinLines = 3, MinDrawn = 2, MaxGap = 2, MaxParts = 4, Slack = 2;

    /// <summary>One drawing: its number in the documents (0 first), and its lines as written.</summary>
    public sealed record Drawing(int Number, IReadOnlyList<string> Lines);

    [GeneratedRegex(@"[-=~_─━═]{3,}")]
    private static partial Regex Rule();

    [GeneratedRegex(@"\+[-=~_]|[-=~_]\+|[┌-╋]")]
    private static partial Regex Corner();

    [GeneratedRegex(@"\[\s+[^\]\[]+?\s+\]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$")]
    private static partial Regex TableRule();

    // A line that is a rule alone: a Markdown section break or a heading's underline. It is drawn, but does not make a drawing on its own.
    [GeneratedRegex(@"^\s*[-=~_*─━═]{3,}\s*$")]
    private static partial Regex RuleAlone();

    [GeneratedRegex(@"^=== .* ===$")]
    private static partial Regex FileHeader();

    /// <summary>
    /// Whether a line is drawn: a rule of three dashes, equals, tildes or underscores (or box-drawing ones); a corner where a rule meets a
    /// plus; two bars or more; or a thing in brackets with spaces inside ("[ HV01 ]"). A Markdown table row is not.
    /// </summary>
    public static bool IsDrawn(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (FileHeader().IsMatch(line.Trim())) return false;
        var bars = line.Count(c => c is '|' or '│' or '║');
        return Rule().IsMatch(line) || Corner().IsMatch(line) || bars >= 2 || Bracketed().IsMatch(line);
    }

    /// <summary>
    /// Whether a line touches a drawing: it holds a bar, a plus, a slash or a box-drawing character (a line down to the next box, a label
    /// beside a line). Such a line never starts a drawing, but counts as part of one it is in.
    /// </summary>
    public static bool Touches(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.IndexOfAny(['|', '+', '/', '\\']) >= 0 || line.Any(c => c is >= '─' and <= '╿');
    }

    /// <summary>
    /// The drawings in the documents, in order: each starts at a drawn line (<see cref="IsDrawn"/>), goes on over drawn lines and lines
    /// that touch it (<see cref="Touches"/>), with at most <see cref="MaxGap"/> other lines in a row, and holds at least
    /// <see cref="MinLines"/> of those, <see cref="MinDrawn"/> of them drawn lines that are not a rule alone (a Markdown section break).
    /// Markdown tables are left out.
    /// </summary>
    public static IReadOnlyList<Drawing> Find(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        // A Markdown table: a run of rows between bars that holds a rule row. Its rows are not drawing.
        var table = new bool[lines.Length];
        for (var i = 0; i < lines.Length;)
        {
            if (!TableRow().IsMatch(lines[i])) { i++; continue; }
            var j = i;
            while (j < lines.Length && TableRow().IsMatch(lines[j])) j++;
            if (Enumerable.Range(i, j - i).Any(k => TableRule().IsMatch(lines[k])))
                for (var k = i; k < j; k++) table[k] = true;
            i = j;
        }
        var found = new List<Drawing>();
        for (var i = 0; i < lines.Length;)
        {
            if (table[i] || !IsDrawn(lines[i])) { i++; continue; }
            var (end, drawn, strong, gap) = (i, 0, 0, 0);
            for (var k = i; k < lines.Length; k++)
            {
                if (!table[k] && IsDrawn(lines[k])) { drawn++; end = k; gap = 0; if (!RuleAlone().IsMatch(lines[k])) strong++; }
                else if (!table[k] && Touches(lines[k])) { drawn++; end = k; gap = 0; }
                else if (++gap > MaxGap) break;
            }
            if (drawn >= MinLines && strong >= MinDrawn) found.Add(new Drawing(found.Count, lines[i..(end + 1)]));
            i = end + 1;
        }
        return found;
    }

    /// <summary>
    /// The drawing in which <paramref name="text"/> is written, or null: on one line as it is (case and runs of spaces aside), or stacked,
    /// as text inside one box: its words in order over lines that follow each other (one blank or drawn-only line may come between), each
    /// part on one line with only spaces between its words, and each starting within <see cref="Slack"/> columns of the part above. All
    /// parts sit in the same box (<see cref="Box"/>), between its top rule and its bottom rule, so a label never crosses a box's rule.
    /// Where the first part sits in no box (a line's label beside it), the parts may not sit in a box either, and are held to 2 to
    /// <see cref="MaxParts"/> lines; <paramref name="fallback"/> then says so. So "vnet-prod 10.50.8.0/22" is found where "vnet-prod" sits
    /// above "10.50.8.0/22" in one box, and words from places further apart are not joined.
    /// </summary>
    public static Drawing? Locate(string text, IReadOnlyList<Drawing> drawings, out bool fallback)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        fallback = false;
        var words = ImportTools.Normalize(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        foreach (var d in drawings)
            for (var i = 0; i < d.Lines.Count; i++)
                if (Stack(d.Lines, i, words, 0, null, null, 0, ref fallback)) return d;
        return null;
    }

    /// <inheritdoc cref="Locate(string, IReadOnlyList{Drawing}, out bool)"/>
    public static Drawing? Locate(string text, IReadOnlyList<Drawing> drawings) => Locate(text, drawings, out _);

    private static bool Stack(IReadOnlyList<string> lines, int line, string[] words, int from, (int Start, int End)? above, (int Top, int Bottom)? box,
        int parts, ref bool fallback)
    {
        if (from == words.Length) return true;
        if (line >= lines.Count || (box is null && parts == MaxParts) || (box is { } b && line >= b.Bottom)) return false;
        for (var to = words.Length; to > from; to--)
        {
            var pattern = new Regex(Edge(words[from], true) + string.Join(@"\s+", words[from..to].Select(Regex.Escape)) + Edge(words[to - 1], false),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            foreach (Match m in pattern.Matches(lines[line]))
            {
                if (above is { } a && Math.Abs(m.Index - a.Start) > Slack) continue;
                var here = Box(lines, line, m.Index, m.Index + m.Length);
                // Every part in the box of the first part, or, where the first is in none, no part in a box.
                if (parts > 0 && here != box) continue;
                if (to == words.Length) { fallback = parts > 0 && box is null; return true; }
                // The next part: on the next line, or one further when the next holds nothing but drawing.
                for (var next = line + 1; next <= line + 2 && next < lines.Count; next++)
                {
                    if (Stack(lines, next, words, to, (m.Index, m.Index + m.Length), here, parts + 1, ref fallback)) return true;
                    if (lines[next].Any(char.IsLetterOrDigit)) break;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The box that columns <paramref name="start"/> to <paramref name="end"/> of a line sit in, as the numbers of its top and bottom rule
    /// lines, or null: the nearest line above whose rule (dashes, equals, tildes, underscores, pluses or box-drawing ones) runs over those
    /// columns, with a side of the box (a bar, a plus or a box-drawing corner) on this line where that rule starts and where it ends, and
    /// the nearest line below whose rule runs over the same columns.
    /// </summary>
    public static (int Top, int Bottom)? Box(IReadOnlyList<string> lines, int line, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var top = -1;
        for (var t = line - 1; t >= 0 && top < 0; t--) if (Covers(lines[t], start, end)) top = t;
        if (top < 0) return null;
        var (left, right) = (start, end - 1);
        while (left > 0 && IsRule(lines[top][left - 1])) left--;
        while (right + 1 < lines[top].Length && IsRule(lines[top][right + 1])) right++;
        if (!IsSide(lines[line], left) || !IsSide(lines[line], right)) return null;
        for (var u = line + 1; u < lines.Count; u++) if (Covers(lines[u], start, end)) return (top, u);
        return null;
    }

    private static bool Covers(string line, int start, int end) => end <= line.Length && Enumerable.Range(start, end - start).All(c => IsRule(line[c]));

    private static bool IsRule(char c) => c is '-' or '=' or '~' or '_' or '+' or '─' or '━' or '═' || (c is >= '┌' and <= '╋' && c is not ('│' or '┃')) || c is >= '╒' and <= '╬';

    private static bool IsSide(string line, int at) => at < line.Length && line[at] is '|' or '+' or '│' or '┃' or '║' or (>= '┌' and <= '╋') or (>= '╒' and <= '╬');

    // A word that starts or ends with a letter or digit is not found inside a longer word ("vm" is not in "vmware").
    private static string Edge(string word, bool start) =>
        start ? (char.IsLetterOrDigit(word[0]) ? @"(?<![\p{L}\p{N}])" : "") : (char.IsLetterOrDigit(word[^1]) ? @"(?![\p{L}\p{N}])" : "");
}
