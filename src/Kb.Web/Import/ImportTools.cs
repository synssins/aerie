using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kb.Core.Ai;
using Kb.Core.Audit;
using Kb.Core.Import;

namespace Kb.Web.Import;

/// <summary>
/// 0.11.0 (draft). The rules for the tools plugins offer the AI during an import (<see cref="ImportToolReg"/>). A tool's name, description
/// and input schema are the only text of a plugin that reaches the model, so they are held to plain text and capped before a tool is
/// offered; a tool that breaks a rule is not offered, and the Admin sees why on the plugin's page.
/// </summary>
public static partial class ImportTools
{
    /// <summary>
    /// Per import item: tool calls that reached a plugin, and calls the app refused before that (the schema, the source, the layout,
    /// repeats); the bytes of one call's input, the length of a tool's description and of its schema, the text
    /// inside its schema (property names, descriptions, choices) and how deep the schema nests.
    /// </summary>
    public const int MaxToolCalls = 8, MaxRefused = 6, MaxInputBytes = 16 * 1024, MaxDescription = 300, MaxSchemaChars = 4000, MaxSchemaText = 1500, MaxDepth = 6,
        MaxPropertyDescription = 200, MaxChoice = 60;

    /// <summary>The input the app adds to every tool: the number of the draft's block the content goes after. The plugin never sees it.</summary>
    public const string Place = "after_block";

    [GeneratedRegex(@"^[a-z][a-z0-9_]{2,39}\z")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,39}\z")]
    private static partial Regex Key();

    // Plain text: letters, digits, spaces and ordinary punctuation. No line breaks, markup, braces, quotes or control characters.
    [GeneratedRegex(@"^[\p{L}\p{N} .,;:!?()/%&+\-]*\z")]
    private static partial Regex Plain();

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "type", "properties", "required", "items", "enum", "minLength", "maxLength", "minItems", "maxItems", "additionalProperties", "description", "format" };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "object", "array", "string", "integer", "number", "boolean" };

    /// <summary>Why the tool may not be offered, or null when it may.</summary>
    public static string? Problem(ImportToolReg tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.Name is null || !Name().IsMatch(tool.Name)) return "Its name is not 3 to 40 lower-case letters, digits or underscores, starting with a letter.";
        if (tool.Description is not { Length: > 0 and <= MaxDescription } d || !Plain().IsMatch(d))
            return $"Its description is not plain text of 1 to {MaxDescription} characters (letters, digits, spaces and ordinary punctuation).";
        if (tool.Tool is null || !typeof(IImportTool).IsAssignableFrom(tool.Tool)) return "It names no tool class.";
        var schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object) return "Its input schema is not a JSON object.";
        if (schema.GetRawText().Length > MaxSchemaChars) return $"Its input schema is longer than {MaxSchemaChars} characters.";
        if (!schema.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "object") return "Its input schema is not of type object.";
        if (schema.TryGetProperty("properties", out var top) && top.ValueKind == JsonValueKind.Object && top.TryGetProperty(Place, out _))
            return $"Its input schema uses the name {Place}, which the app keeps for itself.";
        var texts = new List<string> { tool.Name, d };
        if (Walk(schema, 1, texts) is { } bad) return $"Its input schema: {bad}";
        if (texts.Skip(2).Sum(x => x.Length) > MaxSchemaText) return $"Its input schema holds more than {MaxSchemaText} characters of names, descriptions and choices.";
        if (AiGuard.Tripwire(new Dictionary<string, string> { ["tool"] = string.Join("\n", texts) }) is not null) return "Its text is addressed to the AI.";
        if (tool.For is { } regions && Regions.Problem(regions) is { } why) return $"Its declaration of what it is for: {why}.";
        return null;
    }

    private static string? Walk(JsonElement e, int depth, List<string> texts)
    {
        if (depth > MaxDepth) return $"it nests more than {MaxDepth} deep.";
        if (e.ValueKind != JsonValueKind.Object) return "a part of it is not an object.";
        foreach (var p in e.EnumerateObject())
        {
            if (!Keywords.Contains(p.Name)) return $"\"{(p.Name.Length > 40 ? p.Name[..40] : p.Name)}\" is not a keyword the app allows.";
            var v = p.Value;
            switch (p.Name)
            {
                case "type":
                    if (v.ValueKind != JsonValueKind.String || !Types.Contains(v.GetString()!)) return "a type is not object, array, string, integer, number or boolean.";
                    break;
                case "properties":
                    if (v.ValueKind != JsonValueKind.Object) return "properties is not an object.";
                    foreach (var prop in v.EnumerateObject())
                    {
                        if (!Key().IsMatch(prop.Name)) return "a property name is not 1 to 40 letters, digits or underscores, starting with a letter.";
                        texts.Add(prop.Name);
                        if (Walk(prop.Value, depth + 1, texts) is { } bad) return bad;
                    }
                    break;
                case "required":
                    if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String)) return "required is not a list of names.";
                    if (!e.TryGetProperty("properties", out var props) || v.EnumerateArray().Any(r => !props.TryGetProperty(r.GetString()!, out _)))
                        return "required names a property it does not have.";
                    break;
                case "items":
                    if (Walk(v, depth + 1, texts) is { } bad2) return bad2;
                    break;
                case "enum":
                    if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) return "enum is not a list of choices.";
                    foreach (var c in v.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.Number) continue;
                        if (c.ValueKind != JsonValueKind.String || c.GetString() is not { Length: > 0 and <= MaxChoice } s || !Plain().IsMatch(s))
                            return $"a choice is not plain text of 1 to {MaxChoice} characters, or a number.";
                        texts.Add(s);
                    }
                    break;
                case "format":
                    // A string the model makes up to tie items together (a box's id, the ends of a line): not text of the documents.
                    if (v.ValueKind != JsonValueKind.String || v.GetString() is not ("identifier" or "within" or "enclosed")) return "format may only be identifier, within or enclosed.";
                    if (!e.TryGetProperty("type", out var ft) || ft.GetString() != "string") return "format identifier is only for a string.";
                    break;
                case "additionalProperties":
                    if (v.ValueKind != JsonValueKind.False) return "additionalProperties may only be false.";
                    break;
                case "description":
                    if (v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 and <= MaxPropertyDescription } s2 || !Plain().IsMatch(s2))
                        return $"a description is not plain text of 1 to {MaxPropertyDescription} characters.";
                    texts.Add(s2);
                    break;
                default: // the limits
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0) return $"{p.Name} is not a whole number of 0 or more.";
                    break;
            }
        }
        return null;
    }

    /// <summary>The shortest string the source check applies to, after spaces are collapsed. Shorter ones carry no text of their own.</summary>
    public const int MinGrounded = 3;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,32}\z")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>Text as the source check compares it: lower case, every run of white space one space, trimmed.</summary>
    public static string Normalize(string text) => Space().Replace(AiGuard.Normalize(text ?? "").ToLowerInvariant(), " ").Trim();

    /// <summary>What a string of a tool's input is, for the source check: a choice of the schema, an id the model made up, or text.</summary>
    public enum StringKind { Choice, Identifier, Text }

    /// <summary>Every string of the input, with what its place in the schema makes it.</summary>
    public static IEnumerable<(string Text, StringKind Kind)> Strings(JsonNode? input, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        switch (input)
        {
            case JsonObject o:
                var props = schema["properties"] as JsonObject;
                foreach (var (key, child) in o)
                    foreach (var s in Strings(child, props?[key] as JsonObject ?? [])) yield return s;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var s in Strings(child, schema["items"] as JsonObject ?? [])) yield return s;
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                yield return (v.GetValue<string>(), schema["enum"] is not null ? StringKind.Choice
                    : schema["format"]?.GetValue<string>() is "identifier" or "within" ? StringKind.Identifier : StringKind.Text);
                break;
        }
    }

    /// <summary>
    /// The source check: the first string of the input that is neither in the documents nor in the draft (<paramref name="source"/>,
    /// normalized) nor written in one of the <paramref name="drawings"/> (<see cref="Regions.Locate"/>: on one line, or stacked inside one
    /// box), or null when all are. A string the schema limits to choices (enum) is the schema's, not the model's, and is not checked; nor
    /// is a string shorter than <see cref="MinGrounded"/>. A string marked <c>"format": "identifier"</c> is not checked against the source,
    /// but must be 1 to 32 letters, digits, dots, hyphens or underscores; one that is not is returned. A string found stacked outside any
    /// box (the fallback of <see cref="Regions.Locate(string, IReadOnlyList{Regions.Region}, out bool)"/>) is added to
    /// <paramref name="fallbacks"/>.
    /// </summary>
    public static string? Ungrounded(JsonNode? input, JsonObject schema, string source, IReadOnlyList<Regions.Region>? drawings = null,
        ICollection<string>? fallbacks = null) => UngroundedAll(input, schema, source, drawings, fallbacks, 1).FirstOrDefault();

    /// <summary>As <see cref="Ungrounded"/>, every such string, up to <paramref name="most"/>, so one reply can name them all.</summary>
    public static List<string> UngroundedAll(JsonNode? input, JsonObject schema, string source, IReadOnlyList<Regions.Region>? drawings = null,
        ICollection<string>? fallbacks = null, int most = 5)
    {
        ArgumentNullException.ThrowIfNull(source);
        var found = new List<string>();
        foreach (var (text, kind) in Strings(input, schema))
        {
            if (found.Count >= most) break;
            if (kind == StringKind.Choice) continue;
            if (kind == StringKind.Identifier) { if (!Identifier().IsMatch(text)) found.Add(text); continue; }
            var n = Normalize(text);
            if (n.Length < MinGrounded || source.Contains(n, StringComparison.Ordinal)) continue;
            if (drawings is { Count: > 0 } && Regions.Locate(text, drawings, out var fallback) is not null) { if (fallback) fallbacks?.Add(text); continue; }
            if (!found.Contains(text)) found.Add(text);
        }
        return found;
    }

    /// <summary>
    /// The box check, for a drawing tool: the text strings of one object of the input (such as a box's label and its detail) are read as
    /// a label (the first, in the schema's order) and what is written with it (the others, and the second and further lines of the label).
    /// Where the label is written in a box of a drawing, each of those must be written in the same box. Where it is written in a drawing
    /// but in no box (a thing in brackets, a bare name), each must be written stacked under it, as one label would be
    /// (<see cref="Regions.Locate(string, IReadOnlyList{Regions.Region}, out bool)"/>). A label in no drawing is left to the source check.
    /// Returns the first string that is not where it belongs, with its label, or null.
    /// </summary>
    public static (string Text, string Label)? Misplaced(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> drawings)
    {
        var all = MisplacedAll(input, schema, drawings, 1);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>As <see cref="Misplaced"/>, every such string with its label, up to <paramref name="most"/>, so one reply can name them all.</summary>
    public static List<(string Text, string Label)> MisplacedAll(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> drawings, int most = 5)
    {
        var found = new List<(string, string)>();
        Collect(input, schema, drawings, found, most);
        return found;
    }

    private static void Collect(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> drawings, List<(string, string)> found, int most)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(drawings);
        if (found.Count >= most) return;
        switch (input)
        {
            case JsonObject o:
                var props = schema["properties"] as JsonObject ?? [];
                var texts = props.Select(p => o[p.Key] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                        && Strings(v, p.Value as JsonObject ?? []).First().Kind == StringKind.Text ? v.GetValue<string>() : null)
                    .OfType<string>().ToList();
                if (texts.Count > 0)
                {
                    var lines = texts[0].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var label = lines.Length > 0 ? lines[0] : "";
                    foreach (var text in lines.Skip(1).Concat(texts.Skip(1)))
                        if (found.Count < most && Normalize(text).Length >= MinGrounded && Normalize(label).Length >= MinGrounded && !WithLabel(label, text, drawings))
                            found.Add((text, label));
                }
                foreach (var (key, child) in o)
                    if (child is JsonObject or JsonArray) Collect(child, props[key] as JsonObject ?? [], drawings, found, most);
                break;
            case JsonArray a:
                foreach (var child in a) Collect(child, schema["items"] as JsonObject ?? [], drawings, found, most);
                break;
        }
    }

    /// <summary>
    /// The inside check, for a region tool: an object that names, in a string marked <c>"format": "within"</c>, the id of the object it is
    /// drawn inside (a box in its group) must be written inside the box of that object's label, as the source lays it out. An object's
    /// id is its first string marked <c>"format": "identifier"</c>, its label its first text string, both in the schema's order. A container
    /// whose label stands, in any of the places it is written, in no box (a heading beside a rule), or a name no object declares, is not
    /// checked here. Returns the first
    /// member and container labels that do not stand so, or null.
    /// </summary>
    public static (string Member, string Container)? NotInside(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var objects = new List<(string? Id, string? Label, List<string> Within, bool CanBeWithin)>();
        void Walk(JsonNode? node, JsonObject s)
        {
            switch (node)
            {
                case JsonObject o:
                    var props = s["properties"] as JsonObject ?? [];
                    string? id = null, label = null;
                    var within = new List<string>();
                    var canBeWithin = props.Any(q => q.Value is JsonObject qs && qs["format"]?.GetValue<string>() == "within");
                    foreach (var (key, ps) in props)
                    {
                        if (o[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.String || ps is not JsonObject p) continue;
                        var format = p["format"]?.GetValue<string>();
                        if (format == "identifier") id ??= v.GetValue<string>();
                        else if (format == "within") within.Add(v.GetValue<string>());
                        else if (p["enum"] is null) label ??= v.GetValue<string>().Split('\n')[0];
                    }
                    if (id is not null || within.Count > 0) objects.Add((id, label, within, canBeWithin));
                    foreach (var (key, child) in o) if (child is JsonObject or JsonArray) Walk(child, props[key] as JsonObject ?? []);
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child, s["items"] as JsonObject ?? []);
                    break;
            }
        }
        Walk(input, schema);
        var labels = objects.Where(o => o.Id is not null && o.Label is not null).GroupBy(o => o.Id!).ToDictionary(g => g.Key, g => g.First().Label!);
        foreach (var (_, member, within, _) in objects)
            foreach (var container in within)
            {
                if (member is null || Normalize(member).Length < MinGrounded || !labels.TryGetValue(container, out var outer)) continue;
                // A container written anywhere in no box (a heading beside a rule, or a word that also starts another box's text) is not checked.
                var at = Regions.Starts(outer, regions).ToList();
                if (at.Count == 0) continue;
                if (at.Any(p => p.Box is null))
                {
                    // A container whose name stands in no box (a heading at a rule) heads what is written below it: a member written only
                    // above every place of that name is not in it.
                    var places = Regions.Starts(member, regions).ToList();
                    if (places.Count > 0 && !places.Any(m => at.Any(c => c.Region.Number == m.Region.Number && m.Line >= c.Line))) return (member, outer);
                    continue;
                }
                var frames = at.Select(p => (p.Region.Number, Box: p.Box!.Value)).ToList();
                if (!Regions.Starts(member, regions).Any(m => frames.Any(f => f.Number == m.Region.Number && (m.Box == f.Box || f.Box.Holds(m.Line, m.Column)))))
                    return (member, outer);
            }
        // An item drawn inside the box that holds another object's label, below that label, belongs in that object's group (a server in its
        // network's frame, not only in the frame around that one).
        foreach (var (_, member, within, canBeWithin) in objects)
        {
            if (!canBeWithin || member is null || Normalize(member).Length < MinGrounded) continue;
            // A label written anywhere outside a box (a heading at a rule, or a word that also starts another box's text) is not checked.
            var mine = Regions.Starts(member, regions).ToList();
            if (mine.Count == 0 || mine.Any(p => p.Box is null)) continue;
            foreach (var (otherId, other, _, _) in objects)
            {
                // Naming the object, or another one of the same label (a frame declared both as a box and as a group), is right.
                if (otherId is null || other is null || Normalize(other) == Normalize(member) || within.Contains(otherId)
                    || within.Any(w => labels.TryGetValue(w, out var named) && Normalize(named) == Normalize(other))) continue;
                var holds = Regions.Starts(other, regions).Where(p => p.Box is not null).ToList();
                if (holds.Count > 0 && mine.All(m => holds.Any(h => h.Region.Number == m.Region.Number && h.Box == m.Box && h.Line < m.Line)))
                    return (member, other);
            }
        }
        return null;
    }

    /// <summary>
    /// The enclosed check, for a region tool: a string marked <c>"format": "enclosed"</c> (a box's label) must stand, in the region it is
    /// written in, inside a box or between brackets on its line, when that region draws boxes at all (a chain of bare names does not). So
    /// a line's label beside the line is not taken for a box. Returns the first such string that stands free, or null.
    /// </summary>
    public static string? NotEnclosed(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (var text in Marked(input, schema, "enclosed"))
        {
            if (Normalize(text).Length < MinGrounded) continue;
            var at = Regions.Starts(text, regions).ToList();
            if (at.Count == 0) continue;
            if (at.Any(p => p.Box is not null || Bracketed(p.Region.Lines[p.Line], p.Column) || Joined(p.Region.Lines[p.Line], p.Column, text.Length))) continue;
            if (at.Any(p => p.Region.Lines.Any(l => Corner().IsMatch(l)))) return text;
        }
        return null;
    }

    [GeneratedRegex(@"\+[-=~_]|[-=~_]\+|[┌-╋]")]
    private static partial Regex Corner();

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex BracketItem();

    /// <summary>
    /// For a string marked <c>"format": "enclosed"</c> (a box's label): the first one that joins items the regions write each in its own
    /// brackets (two or more of them, or one with words of its own), or null. Each such item is a box of its own.
    /// </summary>
    public static string? Joins(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var items = regions.SelectMany(r => r.Lines).SelectMany(l => BracketItem().Matches(l).Select(m => Normalize(m.Groups[1].Value)))
            .Where(i => i.Length > 0).Distinct().ToList();
        if (items.Count == 0) return null;
        foreach (var text in Marked(input, schema, "enclosed"))
        {
            var label = Normalize(text.Replace('[', ' ').Replace(']', ' '));
            var hits = items.Where(i => $" {label} ".Contains($" {i} ", StringComparison.Ordinal)).ToList();
            // An item that is part of a longer one ("SQL MI" in "SQL MI replica") counts once, as the longer one.
            hits = [.. hits.Where(i => !hits.Any(o => o != i && $" {o} ".Contains($" {i} ", StringComparison.Ordinal)))];
            if (hits.Count >= 2 || (hits.Count == 1 && hits[0] != label)) return text;
        }
        return null;
    }

    // Whether the text that starts at a column is an end of a line drawn across: a run of two or more dashes, equals or tildes (with or
    // without an arrow head) right before it, or right after it ("|---- Entra ID / M365 / Intune"). A line's label stands beside a line
    // with spaces, not joined to it.
    private static bool Joined(string line, int column, int length)
    {
        var before = line[..column].TrimEnd();
        var after = column + length <= line.Length ? line[(column + length)..].TrimStart() : "";
        return ConnectorEnd().IsMatch(before) || ConnectorStart().IsMatch(after);
    }

    [GeneratedRegex(@"[-=~]{2,}>?$")]
    private static partial Regex ConnectorEnd();

    [GeneratedRegex(@"^<?[-=~]{2,}")]
    private static partial Regex ConnectorStart();

    // Whether the text that starts at a column stands between a "[" before it and a "]" after it, on its line.
    private static bool Bracketed(string line, int column)
    {
        var open = line.LastIndexOf('[', Math.Max(0, column - 1));
        return open >= 0 && line.IndexOf(']', column) is var close and >= 0 && line.IndexOf(']', open) >= column && line.IndexOf('[', open + 1) is var next && (next < 0 || next > close);
    }

    // The strings of the input in places the schema marks with the given format.
    private static IEnumerable<string> Marked(JsonNode? input, JsonObject schema, string format)
    {
        switch (input)
        {
            case JsonObject o:
                var props = schema["properties"] as JsonObject;
                foreach (var (key, child) in o)
                {
                    var s = props?[key] as JsonObject ?? [];
                    if (child is JsonValue v && v.GetValueKind() == JsonValueKind.String && s["format"]?.GetValue<string>() == format) yield return v.GetValue<string>();
                    else foreach (var x in Marked(child, s, format)) yield return x;
                }
                break;
            case JsonArray a:
                foreach (var child in a) foreach (var x in Marked(child, schema["items"] as JsonObject ?? [], format)) yield return x;
                break;
        }
    }

    private static bool WithLabel(string label, string text, IReadOnlyList<Regions.Region> drawings)
    {
        // Text that is part of the label itself (a detail repeating it) stands with it.
        if (Normalize(label).Contains(Normalize(text), StringComparison.Ordinal)) return true;
        var at = Regions.Places(label, drawings).ToList();
        if (at.Count == 0) return true;
        var boxes = at.Where(p => p.Box is not null).Select(p => (p.Region.Number, p.Box)).ToHashSet();
        if (boxes.Count > 0 && Regions.Places(text, drawings).Any(p => boxes.Contains((p.Region.Number, p.Box)))) return true;
        return at.Any(p => p.Box is null) && Regions.Locate(label + " " + text, drawings) is not null;
    }

    [GeneratedRegex(@"^\$(?:\.[A-Za-z_][A-Za-z0-9_]{0,39}|\[[0-9]{1,4}\]){1,12}\z")]
    private static partial Regex JsonPath();

    [GeneratedRegex(@"\.([A-Za-z_][A-Za-z0-9_]*)|\[([0-9]+)\]")]
    private static partial Regex PathStep();

    /// <summary>The node <paramref name="path"/> names in <paramref name="input"/>, or null: a path of the form a tool's refusal may give.</summary>
    public static JsonNode? At(JsonNode? input, string? path)
    {
        if (path is null || !JsonPath().IsMatch(path)) return null;
        var node = input;
        foreach (Match step in PathStep().Matches(path))
            node = step.Groups[1].Success ? (node as JsonObject)?[step.Groups[1].Value]
                : node is JsonArray a && int.TryParse(step.Groups[2].Value, CultureInfo.InvariantCulture, out var i) && i < a.Count ? a[i] : null;
        return node;
    }

    /// <summary>
    /// The app's reply to a tool's refusal (<see cref="ImportToolRefusal"/>), in its own words. The path the tool gave is named only when it
    /// is of the allowed form and names a part of the input; an id there of the identifier form is quoted, being the model's own text.
    /// </summary>
    public static string Refused(ImportToolRefusal why, string? path, JsonNode? input)
    {
        var node = At(input, path);
        var place = node is null ? "the input"
            : node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is var s && Identifier().IsMatch(s) ? $"{path} (\"{s}\")" : path!;
        return why switch
        {
            ImportToolRefusal.UnknownId => $"Refused by the tool: {place} names an id that the input does not declare. Declare it, or leave it out.",
            ImportToolRefusal.DuplicateId => $"Refused by the tool: {place} declares an id that is declared before. Give each its own id.",
            ImportToolRefusal.TooLittle => "Refused by the tool: the input holds too little to make anything of.",
            ImportToolRefusal.TooMuch => "Refused by the tool: the input holds more than the tool can make.",
            ImportToolRefusal.Combined => $"Refused by the tool: {place} holds several items in one text. Give each item on its own.",
            ImportToolRefusal.SelfReference => $"Refused by the tool: {place} refers to the item it belongs to. Leave it out, or name another.",
            _ => "Refused: the tool added nothing.",
        };
    }

    /// <summary>
    /// The region a call of a region tool is about: the one in which most of its text strings (of <see cref="MinGrounded"/> characters or
    /// more) are written, the first on a tie; null when none of them is in any region.
    /// </summary>
    public static Regions.Region? RegionOf(JsonNode? input, JsonObject schema, IReadOnlyList<Regions.Region> drawings)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        return Strings(input, schema).Where(s => s.Kind == StringKind.Text && Normalize(s.Text).Length >= MinGrounded)
            .Select(s => Regions.Locate(s.Text, drawings)).OfType<Regions.Region>()
            .GroupBy(d => d.Number).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.First()).FirstOrDefault();
    }

    /// <summary>The input as one string with its keys in order, so two calls with the same input compare equal however the model ordered them.</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonValue.Create(p.Key).ToJsonString() + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };

    /// <summary>The tool as the chat API offers it: its name, description and schema, with <see cref="Place"/> added and required.</summary>
    public static JsonObject Offer(ImportToolReg tool, int blocks)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var parameters = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        var props = parameters["properties"] as JsonObject ?? [];
        parameters["properties"] = props;
        props[Place] = new JsonObject { ["type"] = "integer", ["description"] = $"The number of the draft block (1 to {blocks}) that shows this content; the result goes right after it." };
        var required = parameters["required"] as JsonArray ?? [];
        required.Add(Place);
        parameters["required"] = required;
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = parameters },
        };
    }

    /// <summary>The draft's blocks for the model, numbered from 1 over all pages, each with the start of its text.</summary>
    public static (string Text, List<(int Page, int Index)> At) Blocks(IReadOnlyList<JsonArray> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var at = new List<(int, int)>();
        var sb = new StringBuilder();
        for (var p = 0; p < pages.Count; p++)
            for (var i = 0; i < pages[p].Count; i++)
            {
                var n = pages[p][i];
                at.Add((p, i));
                var text = Kb.Core.Content.DocText.Plain(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(n!.DeepClone()) }.ToJsonString()).ReplaceLineEndings(" ").Trim();
                sb.Append(CultureInfo.InvariantCulture, $"{at.Count}: ({n["type"]?.GetValue<string>()}) {(text.Length > 100 ? text[..100] + "..." : text)}\n");
            }
        return (sb.ToString(), at);
    }

    /// <summary>The task of the tool conversation: server text. The tools themselves go apart, as the chat API's tools.</summary>
    public const string Task =
        "For this task only, the tools given with this request are available; use them only as this task says, and fetch nothing. " +
        "The documents under \"files\" were imported, and drafted into the page blocks listed under \"blocks\", one per line as number: (kind) start of its text. " +
        "Where the documents hold content that a tool's description fits, call that tool once for that content, with only what the documents say, " +
        "and with after_block the number of the block that shows that content in the draft. Do not call a tool for content it does not fit. " +
        "Each call is answered with done or with the reason it was refused; a refused call may be made again, corrected. " +
        "When no tool fits, or the calls are made, answer with the JSON value {\"done\": true}.";

    /// <summary>One tool call's outcome, for the audit and the importer's warnings.</summary>
    public sealed record Outcome(string PluginId, string Tool, int Bytes, string Result, int Nodes);
}

public sealed partial class ImportService
{
    /// <summary>
    /// 0.11.0 (draft). Offers the model the tools of the loaded plugins over the drafted pages, after the draft contributors. Each call is
    /// checked (a known tool, a JSON object within <see cref="ImportTools.MaxInputBytes"/>, a block number of the draft, the tool's schema),
    /// passed to the plugin with only its input, and audited; what the plugin answers is checked as a contribution and placed after the
    /// block the model named. The model gets only the app's own reply. A model or server that cannot call tools leaves the draft as it is.
    /// </summary>
    /// <returns>The nodes the tools added, each with the plugin and tool that added it and the block it was put after; empty when none.</returns>
    private async Task<List<(JsonNode Node, string PluginId, string Tool, JsonNode? After)>> ToolsAsync(ImportItem item, Actor actor, string source, List<JsonObject> pages,
        List<JsonArray> contents, List<string> warnings, CancellationToken ct)
    {
        var added = new List<(JsonNode Node, string PluginId, string Tool, JsonNode? After)>();
        var tools = new List<(string PluginId, ImportToolReg Tool)>();
        foreach (var (pluginId, tool) in contributors.Tools)
        {
            if (ImportTools.Problem(tool) is { } why) { log.LogWarning("The import tool {Tool} of {Plugin} is not offered: {Why}", tool.Name, pluginId, why); continue; }
            if (tools.Any(t => t.Tool.Name == tool.Name)) { log.LogWarning("The import tool {Tool} of {Plugin} is not offered: another plugin has that name.", tool.Name, pluginId); continue; }
            tools.Add((pluginId, tool));
        }
        // A drawing tool is offered only for documents that hold a drawing, as the app finds it in the source; any other tool as before.
        // A tool with regions is offered only for documents that hold one, as the app finds it in the source; any other tool as before.
        var regionsOf = tools.Where(t => t.Tool.For is not null).ToDictionary(t => t.Tool.Name, t => Regions.Find(source, t.Tool.For!));
        tools.RemoveAll(t => t.Tool.For is not null && regionsOf[t.Tool.Name].Count == 0);
        if (tools.Count == 0 || source.Length == 0) return added;
        var (blocks, at) = ImportTools.Blocks(contents);
        if (at.Count == 0) return added;

        var placed = new List<(int Page, int Index, IReadOnlyList<JsonNode> Nodes)>();
        var by = new Dictionary<JsonNode, (string PluginId, string Tool)>(ReferenceEqualityComparer.Instance);
        // The repeat check: what each tool was given, and after which block, by the calls that added something.
        var made = new HashSet<(string, string)>();
        var places = new HashSet<(string, int)>();
        // A drawing tool: one call that added something for each drawing.
        var drawn = new HashSet<(string, int)>();
        // The source check: the documents and the draft as the model was given them, normalized.
        // A region tool draws what the documents show, so its text must be in the documents themselves, not only in the draft (whose
        // wording is the model's).
        var documents = ImportTools.Normalize(source);
        var grounds = ImportTools.Normalize(source + " " + string.Join(" ", contents.Select(c => Kb.Core.Content.DocText.Plain(new JsonObject { ["type"] = "doc", ["content"] = c.DeepClone() }.ToJsonString()))));
        var outcomes = new List<ImportTools.Outcome>();
        static string Cut(string s) => s.Length > 60 ? s[..60] + "..." : s;
        async Task<AiService.ToolAnswer> AnswerAsync(AiService.ToolCall call)
        {
            var bytes = Encoding.UTF8.GetByteCount(call.Arguments);
            var reached = false;
            async Task<AiService.ToolAnswer> Done(string pluginId, string result, string reply, int nodes = 0)
            {
                outcomes.Add(new ImportTools.Outcome(pluginId, call.Name.Length > 40 ? call.Name[..40] : call.Name, bytes, result, nodes));
                await audit.AppendAsync(new AuditEvent(AuditActions.ImportTool, item.UserId, "importItem", item.Id.ToString(), Data: new Dictionary<string, string>
                {
                    ["plugin"] = pluginId, ["tool"] = call.Name.Length > 40 ? call.Name[..40] : call.Name, ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
                    ["outcome"] = result, ["nodes"] = nodes.ToString(CultureInfo.InvariantCulture),
                }), ct);
                return new AiService.ToolAnswer(reply, reached);
            }
            if (tools.FirstOrDefault(t => t.Tool.Name == call.Name) is not { Tool: not null } found) return await Done("", "unknown-tool", "Refused: there is no tool of that name.");
            if (bytes > ImportTools.MaxInputBytes) return await Done(found.PluginId, "too-large", $"Refused: the input is larger than {ImportTools.MaxInputBytes / 1024} KB.");
            JsonObject? input;
            try { input = JsonNode.Parse(call.Arguments) as JsonObject; }
            catch (JsonException) { input = null; }
            if (input is null) return await Done(found.PluginId, "not-json", "Refused: the input is not a JSON object.");
            if (input[ImportTools.Place] is not JsonValue pv || !pv.TryGetValue<int>(out var after) || after < 1 || after > at.Count)
                return await Done(found.PluginId, "no-block", $"Refused: {ImportTools.Place} must be the number of a block of the draft, 1 to {at.Count}.");
            input.Remove(ImportTools.Place);
            var schema = JsonNode.Parse(found.Tool.InputSchema.GetRawText())!.AsObject();
            if (AiGuard.SchemaError(input, schema) is { } bad)
                return await Done(found.PluginId, "invalid", $"Refused: the input does not match the tool's schema ({(bad.Length > 120 ? bad[..120] : bad)}).");
            var same = ImportTools.Canonical(input);
            var regional = found.Tool.For is not null;
            var regions = regionsOf.GetValueOrDefault(call.Name) ?? [];
            // A region tool is held to one call for each region instead of one for each block: two regions drafted into one block get two.
            if (made.Contains((call.Name, same)) || (!regional && places.Contains((call.Name, after))))
                return await Done(found.PluginId, "repeat", "Refused as a repeat: this tool was already called with this input, or after this block.");
            var fallbacks = new List<string>();
            var loose = ImportTools.UngroundedAll(input, schema, regional ? documents : grounds, regions, fallbacks);
            foreach (var f in fallbacks) log.LogInformation("Import tools for {Item}: a stacked label was found outside any box (the 4-line fallback): {Text}", item.Id, f);
            // Every text the source and layout checks refuse goes in one reply, so the model can correct them all in one turn.
            var apart = regional ? ImportTools.MisplacedAll(input, schema, regions) : [];
            if (loose.Count > 0)
                return await Done(found.PluginId, "not-in-source",
                    $"Refused: the input holds text that is not in the documents or the draft: {string.Join(", ", loose.Select(l => $"\"{Cut(l)}\""))}. Use only text as the documents write it." +
                    string.Concat(apart.Select(m => $" Also, \"{Cut(m.Text)}\" is not written in the same box as \"{Cut(m.Label)}\".")));
            var region = regional ? ImportTools.RegionOf(input, schema, regions) : null;
            if (regional && region is null)
                return await Done(found.PluginId, "no-region", "Refused: the input is about none of the parts of the documents this tool is for. Use the text as they write it.");
            if (apart.Count > 0)
                return await Done(found.PluginId, "not-in-box",
                    $"Refused: {string.Join("; ", apart.Select(m => $"\"{Cut(m.Text)}\" is not written in the same box as \"{Cut(m.Label)}\""))} in the documents. Give each item only the text written with it.");
            if (regional && ImportTools.Joins(input, schema, regions) is { } joined)
                return await Done(found.PluginId, "joins-items",
                    $"Refused: \"{Cut(joined)}\" joins items the documents write each in its own brackets. Give each item a box of its own.");
            if (regional && ImportTools.NotEnclosed(input, schema, regions) is { } free)
                return await Done(found.PluginId, "not-enclosed",
                    $"Refused: \"{Cut(free)}\" is written in no box in the documents, so it is not a box; text beside a line is that line's label.");
            if (regional && ImportTools.NotInside(input, schema, regions) is { } outside)
                return await Done(found.PluginId, "not-inside",
                    $"Refused: \"{Cut(outside.Member)}\" is not drawn inside \"{Cut(outside.Container)}\" in the documents. Put an item in a group only when it is drawn inside it.");
            if (region is not null && drawn.Contains((call.Name, region.Number)))
                return await Done(found.PluginId, "region-done", $"Refused: part {region.Number + 1} of the documents this tool is for already has a result from it.");
            using var doc = JsonDocument.Parse(input.ToJsonString());
            reached = true;
            var (nodes, why, path) = await contributors.CallToolAsync(found.PluginId, found.Tool, item, doc.RootElement.Clone(), ct);
            if (nodes is not { Count: > 0 })
                return await Done(found.PluginId, why == ImportToolRefusal.None ? "nothing" : $"tool-{why.ToString().ToLowerInvariant()}", ImportTools.Refused(why, path, input));
            placed.Add((at[after - 1].Page, at[after - 1].Index, nodes));
            foreach (var n in nodes) by[n] = (found.PluginId, found.Tool.Name);
            made.Add((call.Name, same));
            places.Add((call.Name, after));
            if (region is not null) drawn.Add((call.Name, region.Number));
            return await Done(found.PluginId, "done", $"Done: added after block {after}.", nodes.Count);
        }

        // A region tool that has a result for each of its regions is no longer offered; when no tool is left, the conversation ends.
        JsonArray Offered() => new([.. tools.Where(t => t.Tool.For is null || regionsOf[t.Tool.Name].Any(r => !drawn.Contains((t.Tool.Name, r.Number))))
            .Select(t => (JsonNode)ImportTools.Offer(t.Tool, at.Count))]);
        var data = new Dictionary<string, string> { ["files"] = source, ["blocks"] = blocks };
        var run = await ai.RunToolsAsync(actor, AiFeatures.Import, ImportTools.Task, data, Offered, AnswerAsync, ImportTools.MaxToolCalls, ImportTools.MaxRefused, ct);
        if (run.Problem is { } p)
        {
            warnings.Add($"The plugins' tools could not be offered to the AI, so the draft is as it would be without them: {p.Message ?? "the AI did not answer"}");
            return added;
        }
        // Last block first, so the numbers the model saw still hold; calls after the same block keep their order.
        foreach (var g in placed.Select((x, order) => (x, order)).GroupBy(x => (x.x.Page, x.x.Index)).OrderByDescending(g => g.Key.Page).ThenByDescending(g => g.Key.Index))
        {
            var content = contents[g.Key.Page];
            var after = content[g.Key.Index];
            var at2 = g.Key.Index + 1;
            foreach (var n in g.OrderBy(x => x.order).SelectMany(x => x.x.Nodes)) { content.Insert(at2++, n); added.Add((n, by[n].PluginId, by[n].Tool, after)); }
        }
        foreach (var o in outcomes.Where(o => o.Result == "done"))
            warnings.Add($"{contributors.PluginName(o.PluginId)} added {o.Nodes} block{(o.Nodes == 1 ? "" : "s")} through its tool {o.Tool}.");
        if (outcomes.Count(o => o.Result != "done") is > 0 and var refused)
            warnings.Add($"The AI's tool calls were refused {refused} time{(refused == 1 ? "" : "s")}: {string.Join(", ", outcomes.Where(o => o.Result != "done").Select(o => o.Result).Distinct())}.");
        if (run.Value!.LimitReached)
            warnings.Add($"The AI reached the limit of tool calls for this item ({ImportTools.MaxToolCalls} answered by a plugin, {ImportTools.MaxRefused} refused by the app).");
        log.LogInformation("Import tools for {Item}: {Calls} model call(s), {Tools} tool call(s), {In}+{Out} tokens, {Ms} ms.", item.Id, run.Value.ModelCalls, run.Value.ToolCalls,
            run.Value.PromptTokens, run.Value.CompletionTokens, run.Value.Milliseconds);
        return added;
    }
}
