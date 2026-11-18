using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kb.Core.Search;

namespace Kb.Web.DataCenter;

// The CQL subset of T1 (contracts/behaviour/search-cql.md): clauses joined by AND and OR left to right, NOT, brackets, ORDER BY, the T1 fields,
// operators, values and functions. A query is parsed to a tree, then run over the items the caller may see.

public sealed class CqlException(string message) : Exception(message);

/// <summary>One item search runs over: a page or folder, or an attachment.</summary>
public sealed record CqlItem(Guid Id, long DcId, string Type, Guid SpaceId, string SpaceKey, string SpaceName, bool PersonalSpace, string Title, string Text,
    IReadOnlyList<string> Labels, string CreatorKey, string CreatorName, IReadOnlyList<(string Key, string Name)> Contributors, DateTimeOffset Created, DateTimeOffset Modified,
    long? Parent, IReadOnlyList<long> Ancestors, long? Container);

/// <summary>What the functions need: the caller, the context, and now.</summary>
public sealed record CqlContext(string? UserKey, string? UserName, string? SpaceKey, DateTimeOffset Now);

public abstract record CqlNode;
public sealed record CqlAnd(CqlNode Left, CqlNode Right) : CqlNode;
public sealed record CqlOr(CqlNode Left, CqlNode Right) : CqlNode;
public sealed record CqlNot(CqlNode Inner) : CqlNode;
public sealed record CqlClause(string Field, string Op, IReadOnlyList<CqlValue> Values) : CqlNode;
/// <summary>A value: text as written (unquoted), whether it was quoted, or a function call.</summary>
public sealed record CqlValue(string Text, bool Quoted, string? Function = null, IReadOnlyList<string>? Args = null);
public sealed record CqlQuery(CqlNode Where, IReadOnlyList<(string Field, bool Descending)> Order);

public static partial class Cql
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "after", "and", "as", "avg", "before", "begin", "by", "commit", "contains", "count", "distinct", "else", "empty", "end", "explain", "from", "having", "if", "in",
        "inner", "insert", "into", "is", "isnull", "left", "like", "limit", "max", "min", "not", "null", "or", "order", "outer", "right", "select", "sum", "then", "was",
        "where", "update",
    };

    private enum Kind { Id, Date, Text, User, Plain, SpaceType, Type }

    private static readonly Dictionary<string, Kind> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = Kind.Id, ["content"] = Kind.Id, ["type"] = Kind.Type, ["space"] = Kind.Plain, ["space.key"] = Kind.Plain, ["space.type"] = Kind.SpaceType,
        ["space.title"] = Kind.Text, ["title"] = Kind.Text, ["text"] = Kind.Text, ["label"] = Kind.Plain, ["creator"] = Kind.User, ["contributor"] = Kind.User,
        ["created"] = Kind.Date, ["lastmodified"] = Kind.Date, ["ancestor"] = Kind.Id, ["parent"] = Kind.Id, ["container"] = Kind.Id,
    };

    private static readonly string[] Deferred = ["macro", "mention", "watcher", "favourite", "favorite", "space.category"];
    private static readonly string[] Orderable = ["created", "lastmodified", "title", "id", "type", "space"];

    // ---- tokens ----

    private sealed record Token(string Text, bool Quoted, int At);

    private static List<Token> Tokens(string cql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < cql.Length)
        {
            var c = cql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '"' or '\'')
            {
                var sb = new StringBuilder();
                var start = i++;
                while (true)
                {
                    if (i >= cql.Length) throw new CqlException($"The quote at {start} is not closed.");
                    if (cql[i] == '\\' && i + 1 < cql.Length) { sb.Append(cql[i + 1]); i += 2; continue; }
                    if (cql[i] == c) { i++; break; }
                    sb.Append(cql[i++]);
                }
                tokens.Add(new Token(sb.ToString(), true, start));
                continue;
            }
            if (c is '(' or ')' or ',') { tokens.Add(new Token(c.ToString(), false, i++)); continue; }
            if (c is '!' or '<' or '>' or '=' or '~')
            {
                var two = i + 1 < cql.Length ? cql.Substring(i, 2) : "";
                var op = two is "!=" or "!~" or ">=" or "<=" ? two : c.ToString();
                if (op == "!") throw new CqlException($"! at {i} is not an operator.");
                tokens.Add(new Token(op, false, i));
                i += op.Length;
                continue;
            }
            var from = i;
            while (i < cql.Length && !char.IsWhiteSpace(cql[i]) && cql[i] is not ('(' or ')' or ',' or '"' or '\'' or '!' or '<' or '>' or '=' or '~')) i++;
            tokens.Add(new Token(cql[from..i], false, from));
        }
        return tokens;
    }

    // ---- parsing ----

    public static CqlQuery Parse(string cql)
    {
        if (string.IsNullOrWhiteSpace(cql)) throw new CqlException("cql is required.");
        var t = Tokens(cql);
        var i = 0;
        if (t.Count > 0 && Is(t[0], "NOT")) throw new CqlException("A query cannot start with NOT.");
        var where = Expr(t, ref i, 0);
        var order = new List<(string, bool)>();
        if (i < t.Count && Is(t[i], "ORDER"))
        {
            i++;
            if (i >= t.Count || !Is(t[i], "BY")) throw new CqlException("ORDER is followed by BY.");
            i++;
            while (true)
            {
                if (i >= t.Count) throw new CqlException("ORDER BY needs a field.");
                var field = t[i++].Text.ToLowerInvariant();
                if (field == "label") throw new CqlException("label holds several values and cannot be ordered by.");
                if (!Orderable.Contains(field)) throw new CqlException($"{field} cannot be ordered by.");
                var desc = false;
                if (i < t.Count && (Is(t[i], "ASC") || Is(t[i], "DESC"))) desc = Is(t[i++], "DESC");
                order.Add((field, desc));
                if (i < t.Count && t[i].Text == ",") { i++; continue; }
                break;
            }
        }
        if (i < t.Count) throw new CqlException($"Unexpected {t[i].Text} at {t[i].At}.");
        return new CqlQuery(where, order);
    }

    private static bool Is(Token t, string word) => !t.Quoted && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Terms joined by AND and OR, left to right, with no precedence between them.</summary>
    private static CqlNode Expr(List<Token> t, ref int i, int depth)
    {
        var left = Term(t, ref i, depth);
        while (i < t.Count && (Is(t[i], "AND") || Is(t[i], "OR")))
        {
            var and = Is(t[i++], "AND");
            var right = Term(t, ref i, depth);
            left = and ? new CqlAnd(left, right) : new CqlOr(left, right);
        }
        return left;
    }

    /// <summary>How deep brackets and NOT may nest. The parser calls itself once per level, and a stack overflow ends the process.</summary>
    public const int MaxDepth = 50;

    private static CqlNode Term(List<Token> t, ref int i, int depth)
    {
        if (i >= t.Count) throw new CqlException("The query ends where a clause is expected.");
        if (depth > MaxDepth) throw new CqlException($"Brackets and NOT are nested more than {MaxDepth} deep.");
        if (Is(t[i], "NOT")) { i++; return new CqlNot(Term(t, ref i, depth + 1)); }
        if (t[i].Text == "(" && !t[i].Quoted)
        {
            i++;
            var inner = Expr(t, ref i, depth + 1);
            if (i >= t.Count || t[i].Text != ")") throw new CqlException("A bracket is not closed.");
            i++;
            return inner;
        }
        return Clause(t, ref i);
    }

    private static CqlClause Clause(List<Token> t, ref int i)
    {
        var fieldToken = t[i++];
        if (fieldToken.Quoted || fieldToken.Text is "(" or ")") throw new CqlException($"A field is expected at {fieldToken.At}.");
        var field = fieldToken.Text.ToLowerInvariant();
        if (Deferred.Contains(field)) throw new CqlException($"The field {field} is not supported.");
        if (!Fields.TryGetValue(field, out var kind)) throw new CqlException($"{fieldToken.Text} is not a field aerie supports.");
        if (i >= t.Count) throw new CqlException($"{field} needs an operator.");
        string op;
        if (Is(t[i], "NOT") && i + 1 < t.Count && Is(t[i + 1], "IN")) { op = "NOT IN"; i += 2; }
        else if (Is(t[i], "IN")) { op = "IN"; i++; }
        else if (!t[i].Quoted && t[i].Text is "=" or "!=" or ">" or ">=" or "<" or "<=" or "~" or "!~") op = t[i++].Text;
        else throw new CqlException($"{t[i].Text} is not an operator.");
        CheckOperator(field, kind, op);
        var values = new List<CqlValue>();
        if (op is "IN" or "NOT IN")
        {
            if (i >= t.Count || t[i].Text != "(") throw new CqlException($"{op} takes a bracketed list.");
            i++;
            while (true)
            {
                values.Add(Value(t, ref i));
                if (i < t.Count && t[i].Text == ",") { i++; continue; }
                if (i < t.Count && t[i].Text == ")") { i++; break; }
                throw new CqlException("A list is not closed.");
            }
        }
        else values.Add(Value(t, ref i));
        return new CqlClause(field, op, values);
    }

    private static CqlValue Value(List<Token> t, ref int i)
    {
        if (i >= t.Count) throw new CqlException("A value is expected.");
        var v = t[i++];
        if (v.Quoted) return new CqlValue(v.Text, true);
        if (v.Text is "(" or ")" or ",") throw new CqlException($"A value is expected at {v.At}.");
        if (i < t.Count && t[i].Text == "(" && !t[i].Quoted)
        {
            i++;
            var args = new List<string>();
            while (i < t.Count && t[i].Text != ")")
            {
                if (t[i].Text != ",") args.Add(t[i].Text);
                i++;
            }
            if (i >= t.Count) throw new CqlException($"The call of {v.Text} is not closed.");
            i++;
            return new CqlValue(v.Text, false, v.Text, args);
        }
        if (Reserved.Contains(v.Text)) throw new CqlException($"{v.Text} is a reserved word; quote it to use it as a value.");
        return new CqlValue(v.Text, false);
    }

    private static void CheckOperator(string field, Kind kind, string op)
    {
        var ok = kind switch
        {
            Kind.Text => field == "title" ? op is "=" or "!=" or "~" or "!~" or "IN" or "NOT IN" : op is "~" or "!~",
            Kind.Date => op is "=" or "!=" or ">" or ">=" or "<" or "<=",
            _ => op is "=" or "!=" or "IN" or "NOT IN",
        };
        if (!ok) throw new CqlException($"{field} does not take {op}.");
    }

    // ---- running ----

    /// <summary>Whether the item matches; <paramref name="score"/> counts the text terms it matched, for relevance.</summary>
    public static bool Matches(CqlNode node, CqlItem item, CqlContext ctx, ref int score) => node switch
    {
        CqlAnd a => Matches(a.Left, item, ctx, ref score) & Matches(a.Right, item, ctx, ref score),
        CqlOr o => Matches(o.Left, item, ctx, ref score) | Matches(o.Right, item, ctx, ref score),
        CqlNot n => !MatchesQuiet(n.Inner, item, ctx),
        CqlClause c => Clause(c, item, ctx, ref score),
        _ => false,
    };

    private static bool MatchesQuiet(CqlNode node, CqlItem item, CqlContext ctx)
    {
        var ignored = 0;
        return Matches(node, item, ctx, ref ignored);
    }

    private static bool Clause(CqlClause c, CqlItem item, CqlContext ctx, ref int score)
    {
        var negative = c.Op is "!=" or "NOT IN" or "!~";
        bool any;
        switch (Fields[c.Field])
        {
            case Kind.Date:
                return DateClause(c, c.Field == "created" ? item.Created : item.Modified, ctx);
            case Kind.Text:
                var target = c.Field switch { "title" => item.Title, "space.title" => item.SpaceName, _ => item.Title + " " + item.Text + " " + string.Join(' ', item.Labels) };
                if (c.Op is "~" or "!~")
                {
                    var hit = TextMatch(c.Values[0].Text, target, out var hits);
                    if (hit && !negative) score += c.Field == "title" ? hits * 3 : hits;
                    return hit != negative;
                }
                any = c.Values.Any(v => string.Equals(v.Text, item.Title, StringComparison.OrdinalIgnoreCase));
                break;
            case Kind.Id:
                var ids = c.Field switch
                {
                    "ancestor" => item.Ancestors,
                    "parent" => item.Parent is { } p ? [p] : [],
                    "container" => item.Container is { } k ? [k] : [],
                    _ => (IReadOnlyList<long>)[item.DcId],
                };
                any = c.Values.Any(v => long.TryParse(Plain(v, ctx), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? ids.Contains(n)
                    : throw new CqlException($"{c.Field} takes content ids, decimal digits."));
                break;
            case Kind.Type:
                any = c.Values.Any(v => string.Equals(Plain(v, ctx), item.Type, StringComparison.OrdinalIgnoreCase));
                break;
            case Kind.SpaceType:
                // favourite: aerie has no favourite spaces, so it matches none.
                any = c.Values.Any(v => Plain(v, ctx).ToLowerInvariant() switch { "personal" => item.PersonalSpace, "global" => !item.PersonalSpace, _ => false });
                break;
            case Kind.User:
                var people = c.Field == "creator" ? [(item.CreatorKey, item.CreatorName)] : item.Contributors;
                any = c.Values.Any(v =>
                {
                    if (v.Function is { } f)
                    {
                        if (!f.Equals("currentUser", StringComparison.OrdinalIgnoreCase)) throw new CqlException($"{f}() cannot be used with {c.Field}.");
                        return ctx.UserKey is { } key && people.Any(u => u.Key == key); // nobody signed in matches nothing
                    }
                    return people.Any(u => u.Key == v.Text || string.Equals(u.Name, v.Text, StringComparison.OrdinalIgnoreCase));
                });
                break;
            default: // space, space.key, label
                var values = c.Values.Select(v => v.Function is { } fn ? SpaceFunction(fn, c.Field, ctx) : v.Text).Select(v => v.ToLowerInvariant()).ToList();
                if (c.Field == "label")
                {
                    // my:name is a person's own label; aerie's labels are all global, so it matches none.
                    any = values.Any(v => !v.StartsWith("my:", StringComparison.Ordinal) && item.Labels.Contains(v.StartsWith("global:", StringComparison.Ordinal) ? v[7..] : v));
                }
                else any = values.Contains(item.SpaceKey.ToLowerInvariant());
                break;
        }
        return any != negative;
    }

    private static string Plain(CqlValue v, CqlContext ctx) => v.Function is { } f ? throw new CqlException($"{f}() is not used here.") : v.Text;

    private static string SpaceFunction(string f, string field, CqlContext ctx)
    {
        if (!f.Equals("currentSpace", StringComparison.OrdinalIgnoreCase) || field == "label") throw new CqlException($"{f}() cannot be used with {field}.");
        return ctx.SpaceKey ?? throw new CqlException("currentSpace() needs a spaceKey in cqlcontext.");
    }

    // ---- dates ----

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm"];

    private static bool DateClause(CqlClause c, DateTimeOffset at, CqlContext ctx)
    {
        var v = c.Values[0];
        DateTimeOffset start;
        TimeSpan span;
        if (v.Function is { } f) (start, span) = (DateFunction(f, v.Args ?? [], ctx), TimeSpan.Zero);
        else if (DateTimeOffset.TryParseExact(v.Text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            (start, span) = (d, v.Text.Contains(':', StringComparison.Ordinal) ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1));
        else throw new CqlException($"{v.Text} is not a date: yyyy-MM-dd or yyyy/MM/dd, optionally with HH:mm.");
        // A date with no time means that whole day for = and !=, and its start for the ranges.
        return c.Op switch
        {
            "=" => span == TimeSpan.Zero ? at == start : at >= start && at < start + span,
            "!=" => span == TimeSpan.Zero ? at != start : at < start || at >= start + span,
            ">" => at > start, ">=" => at >= start, "<" => at < start, "<=" => at <= start,
            _ => false,
        };
    }

    [GeneratedRegex(@"^([+-]?)(\d+)([yMwdhm])$")]
    private static partial Regex Increment();

    /// <summary>now(inc) and the start or end of the day, week (from Monday), month or year, moved by inc ("-4w", "+1d", "2M").</summary>
    private static DateTimeOffset DateFunction(string f, IReadOnlyList<string> args, CqlContext ctx)
    {
        var now = ctx.Now.ToUniversalTime();
        var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var week = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var year = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var baseValue = f.ToLowerInvariant() switch
        {
            "now" => now,
            "startofday" => day, "endofday" => day.AddDays(1).AddTicks(-1),
            "startofweek" => week, "endofweek" => week.AddDays(7).AddTicks(-1),
            "startofmonth" => month, "endofmonth" => month.AddMonths(1).AddTicks(-1),
            "startofyear" => year, "endofyear" => year.AddYears(1).AddTicks(-1),
            _ => throw new CqlException($"{f}() is not a date function aerie supports."),
        };
        if (args.Count == 0 || args[0].Length == 0) return baseValue;
        var m = Increment().Match(args[0].Trim('"', '\''));
        if (!m.Success) throw new CqlException($"{args[0]} is not an increment such as \"-4w\".");
        var n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * (m.Groups[1].Value == "-" ? -1 : 1);
        return m.Groups[3].Value switch
        {
            "y" => baseValue.AddYears(n), "M" => baseValue.AddMonths(n), "w" => baseValue.AddDays(7 * n), "d" => baseValue.AddDays(n), "h" => baseValue.AddHours(n), _ => baseValue.AddMinutes(n),
        };
    }

    // ---- text ----

    [GeneratedRegex(@"""([^""]*)""|(\S+)")]
    private static partial Regex Terms();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    /// <summary>
    /// Every word, after aerie's own stemming; a quoted phrase in order; ? and * inside a word as wildcards; word~ loosely (one letter off).
    /// <paramref name="hits"/> counts the terms found.
    /// </summary>
    public static bool TextMatch(string value, string target, out int hits)
    {
        hits = 0;
        var tokens = SearchText.Tokenize(target).ToList();
        var set = tokens.ToHashSet(StringComparer.Ordinal);
        var raw = Words().Matches(target.ToLowerInvariant()).Select(m => m.Value).ToList();
        var any = false;
        foreach (Match m in Terms().Matches(value))
        {
            any = true;
            bool found;
            if (m.Groups[1].Success)
            {
                var phrase = SearchText.Tokenize(m.Groups[1].Value).ToList();
                found = phrase.Count == 0 || Enumerable.Range(0, Math.Max(0, tokens.Count - phrase.Count + 1)).Any(i => tokens.Skip(i).Take(phrase.Count).SequenceEqual(phrase));
            }
            else
            {
                var word = m.Groups[2].Value.ToLowerInvariant();
                if (word.EndsWith('~'))
                {
                    var loose = word.TrimEnd('~');
                    found = raw.Any(r => Distance(r, loose) <= 1);
                }
                else if (word.Length > 1 && (word[1..].Contains('*', StringComparison.Ordinal) || word[1..].Contains('?', StringComparison.Ordinal)))
                {
                    var pattern = new Regex("^" + Regex.Escape(word).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.None, TimeSpan.FromSeconds(1));
                    found = raw.Any(r => pattern.IsMatch(r));
                }
                else
                {
                    var stems = SearchText.Tokenize(word).ToList();
                    found = stems.Count == 0 || stems.All(set.Contains);
                }
            }
            if (!found) return false;
            hits++;
        }
        return any;
    }

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>The words of the query's text clauses, for highlighting.</summary>
    public static IReadOnlyList<string> TextWords(CqlNode node) => node switch
    {
        CqlAnd a => [.. TextWords(a.Left), .. TextWords(a.Right)],
        CqlOr o => [.. TextWords(o.Left), .. TextWords(o.Right)],
        CqlClause { Op: "~" } c => [.. Words().Matches(c.Values[0].Text.ToLowerInvariant()).Select(m => m.Value)],
        _ => [],
    };

    /// <summary>cqlcontext: a JSON object with spaceKey and contentId. Other keys are ignored.</summary>
    public static (string? SpaceKey, string? ContentId) Context(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new CqlException("cqlcontext is a JSON object.");
            string? Read(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;
            return (Read("spaceKey"), Read("contentId"));
        }
        catch (JsonException) { throw new CqlException("cqlcontext is not valid JSON."); }
    }
}
