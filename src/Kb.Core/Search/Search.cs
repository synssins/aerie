using System.Text;

namespace Kb.Core.Search;

/// <summary>What to search for. <see cref="SpaceIds"/> is already trimmed to what the caller may read.</summary>
public sealed record SearchQuery(string Text, IReadOnlyList<Guid> SpaceIds, string? Tag = null, int Limit = 20, int Offset = 0);

public sealed record SearchHit(Guid PageId, double Score, IReadOnlyList<string> Matched);

public sealed record SearchResult(int Total, IReadOnlyList<SearchHit> Hits);

/// <summary>
/// The search engine behind the search box and the agent API. The built-in one is SQL with typo tolerance and synonyms;
/// a plugin can supply another (vector, hosted) without the callers changing.
/// </summary>
public interface ISearchProvider
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
}

public static class SearchText
{
    public const int MaxTermLength = 40;

    /// <summary>Lowercase words and numbers, 2 to 40 characters.</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) { if (sb.Length < MaxTermLength) sb.Append(char.ToLowerInvariant(ch)); }
            else if (sb.Length > 0) { if (sb.Length >= 2) yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length >= 2) yield return sb.ToString();
    }

    /// <summary>Edits allowed for a typo: none under 4 letters, one up to 7, two from 8.</summary>
    public static int Tolerance(string term) => term is null || term.Length < 4 ? 0 : term.Length < 8 ? 1 : 2;

    /// <summary>Edit distance, or max + 1 as soon as it is known to exceed max.</summary>
    public static int Distance(string a, string b, int max)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, cur[j]);
            }
            if (best > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Synonym groups, one per line, comma separated: "password, pw, passcode".</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseSynonyms(string? text)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var group = line.Split(',').Select(w => string.Join(' ', Tokenize(w))).Where(w => w.Length > 0).Distinct().ToList();
            if (group.Count < 2) continue;
            foreach (var w in group) map[w] = group.Where(o => o != w).ToList();
        }
        return map;
    }

    public const string DefaultSynonyms = """
        password, pw, passcode
        login, logon, signin
        computer, pc, workstation, laptop
        printer, mfp, copier
        wifi, wireless, wlan
        vpn, remote access
        mfa, 2fa, authenticator
        email, mail, outlook
        phone, extension, voip
        reset, unlock
        """;

    /// <summary>About 200 characters around the first matched term.</summary>
    public static string Snippet(string? text, IEnumerable<string> terms)
    {
        if (string.IsNullOrEmpty(text)) return "";
        ArgumentNullException.ThrowIfNull(terms);
        var flat = text.Replace('\n', ' ');
        var at = terms.Select(t => flat.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 60);
        if (start > 0) { var sp = flat.IndexOf(' ', start); if (sp >= 0 && sp < at) start = sp + 1; }
        var len = Math.Min(200, flat.Length - start);
        return (start > 0 ? "…" : "") + flat.Substring(start, len).Trim() + (start + len < flat.Length ? "…" : "");
    }
}
