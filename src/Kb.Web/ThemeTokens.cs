using System.Text.Json;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Data;
using Kb.Web.Import;

namespace Kb.Web;

/// <summary>
/// The color tokens an Admin may override (Administration > Branding > Theme colors), the same names as the client's
/// themeTokens.ts (a test checks they match). A theme stores only overrides, for Day and for Night; the rest is theme.css's.
/// </summary>
public static partial class ThemeTokens
{
    public const string SettingKey = "Brand.Theme";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "--nav", "--nav-fg", "--nav-ink", "--nav-hover", "--nav-active", "--logo-bg", "--logo-ink", "--search-bg", "--search-ink", "--search-ink2", "--search-line",
        "--avatar-bg", "--avatar-ink", "--badge-bg", "--badge-ink",
        "--stage", "--paper", "--rail", "--line", "--hover", "--toolbar-bg", "--switch-knob", "--scrollbar-thumb", "--scrollbar-track",
        "--ink", "--ink2", "--link", "--folder",
        "--accent", "--accent-ink", "--cur-bg", "--focus", "--selection", "--btn-primary", "--btn-primary-ink",
        "--warn-bg", "--warn-line", "--warn-ink", "--danger", "--danger-ink", "--success", "--removed",
        "--tag-bg", "--tag-ink", "--code-bg", "--code-ink", "--code-comment", "--code-keyword", "--code-string", "--code-number", "--code-variable", "--code-title",
        "--code-deletion", "--code-meta",
    };

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex Hex();

    /// <summary>#abc as #aabbcc, lower case.</summary>
    public static string Normalize(string hex)
    {
        var h = hex.Trim()[1..].ToLowerInvariant();
        return "#" + (h.Length == 3 ? string.Concat(h.Select(c => $"{c}{c}")) : h);
    }

    /// <summary>The overrides as they will be stored, or the first problem: an unknown token or a value that is not #rgb or #rrggbb.</summary>
    public static (ThemeDto? Theme, string? Field, string? Error) Check(ThemeDto? given)
    {
        var day = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var night = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (mode, from, into) in new[] { ("day", given?.Day, day), ("night", given?.Night, night) })
            foreach (var (k, v) in from ?? new Dictionary<string, string>())
            {
                if (!All.Contains(k)) return (null, $"{mode}.{k}", "Not a theme color.");
                if (v is null || !Hex().IsMatch(v.Trim())) return (null, $"{mode}.{k}", "A color as #rgb or #rrggbb.");
                into[k] = Normalize(v);
            }
        return (new ThemeDto(day, night), null, null);
    }

    public static async Task<ThemeDto?> ReadAsync(KbDbContext db, CancellationToken ct)
    {
        if (await db.GetAsync(SettingKey, ct) is not { Length: > 0 } json) return null;
        try { return Check(JsonSerializer.Deserialize<ThemeDto>(json, JsonSerializerOptions.Web)).Theme; }
        catch (JsonException) { return null; }
    }
}

/// <summary>The theme colors an Admin sets. Core only.</summary>
[Action("brand.theme.save")]
public sealed record SaveTheme : IAction<ThemeDto>
{
    public required ThemeDto Theme { get; init; }
}

/// <summary>Saves the theme colors: what is given replaces what was stored; both empty is the default theme.</summary>
internal sealed class SaveThemeHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SaveTheme, ThemeDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveTheme> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<ThemeDto>> HandleAsync(ActionContext<SaveTheme> ctx)
    {
        var (theme, field, error) = ThemeTokens.Check(ctx.Action.Theme);
        if (error is not null) return Problem.Invalid(field!, error);
        var before = await ThemeTokens.ReadAsync(db, ctx.Cancel) ?? new ThemeDto(new Dictionary<string, string>(), new Dictionary<string, string>());
        var empty = theme!.Day.Count == 0 && theme.Night.Count == 0;
        await db.SetAsync(ThemeTokens.SettingKey, empty ? null : JsonSerializer.Serialize(theme, JsonSerializerOptions.Web), ctx.Actor.UserId, time.GetUtcNow());
        // Which tokens changed, in which mode, and their new values: colors are not secrets.
        static IEnumerable<string> Diff(string mode, IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
            a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).Order(StringComparer.Ordinal).Select(k => $"{mode} {k}={b.GetValueOrDefault(k) ?? "default"}");
        var changes = Diff("day", before.Day, theme.Day).Concat(Diff("night", before.Night, theme.Night)).ToList();
        ctx.Audit(AuditActions.BrandTheme, "brand", "theme", data: new Dictionary<string, string>
        {
            ["changes"] = string.Join("; ", changes).Truncate(3000), ["overrides"] = (theme.Day.Count + theme.Night.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["default"] = empty.ToString(),
        });
        return theme;
    }
}
