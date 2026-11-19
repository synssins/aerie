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
    /// Per import item: tool calls answered, the bytes of one call's input, the length of a tool's description and of its schema, the text
    /// inside its schema (property names, descriptions, choices) and how deep the schema nests.
    /// </summary>
    public const int MaxToolCalls = 8, MaxInputBytes = 16 * 1024, MaxDescription = 300, MaxSchemaChars = 4000, MaxSchemaText = 1500, MaxDepth = 6,
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
        { "type", "properties", "required", "items", "enum", "minLength", "maxLength", "minItems", "maxItems", "additionalProperties", "description" };

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
    private async Task ToolsAsync(ImportItem item, Actor actor, string source, List<JsonObject> pages, List<JsonArray> contents, List<string> warnings, CancellationToken ct)
    {
        var tools = new List<(string PluginId, ImportToolReg Tool)>();
        foreach (var (pluginId, tool) in contributors.Tools)
        {
            if (ImportTools.Problem(tool) is { } why) { log.LogWarning("The import tool {Tool} of {Plugin} is not offered: {Why}", tool.Name, pluginId, why); continue; }
            if (tools.Any(t => t.Tool.Name == tool.Name)) { log.LogWarning("The import tool {Tool} of {Plugin} is not offered: another plugin has that name.", tool.Name, pluginId); continue; }
            tools.Add((pluginId, tool));
        }
        if (tools.Count == 0 || source.Length == 0) return;
        var (blocks, at) = ImportTools.Blocks(contents);
        if (at.Count == 0) return;

        var placed = new List<(int Page, int Index, IReadOnlyList<JsonNode> Nodes)>();
        var outcomes = new List<ImportTools.Outcome>();
        async Task<string> AnswerAsync(AiService.ToolCall call)
        {
            var bytes = Encoding.UTF8.GetByteCount(call.Arguments);
            async Task<string> Done(string pluginId, string result, string reply, int nodes = 0)
            {
                outcomes.Add(new ImportTools.Outcome(pluginId, call.Name.Length > 40 ? call.Name[..40] : call.Name, bytes, result, nodes));
                await audit.AppendAsync(new AuditEvent(AuditActions.ImportTool, item.UserId, "importItem", item.Id.ToString(), Data: new Dictionary<string, string>
                {
                    ["plugin"] = pluginId, ["tool"] = call.Name.Length > 40 ? call.Name[..40] : call.Name, ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
                    ["outcome"] = result, ["nodes"] = nodes.ToString(CultureInfo.InvariantCulture),
                }), ct);
                return reply;
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
            using var doc = JsonDocument.Parse(input.ToJsonString());
            var nodes = await contributors.CallToolAsync(found.PluginId, found.Tool, item, doc.RootElement.Clone(), ct);
            if (nodes is not { Count: > 0 }) return await Done(found.PluginId, "nothing", "Refused: the tool added nothing.");
            placed.Add((at[after - 1].Page, at[after - 1].Index, nodes));
            return await Done(found.PluginId, "done", $"Done: added after block {after}.", nodes.Count);
        }

        var data = new Dictionary<string, string> { ["files"] = source, ["blocks"] = blocks };
        var run = await ai.RunToolsAsync(actor, AiFeatures.Import, ImportTools.Task, data, new JsonArray([.. tools.Select(t => (JsonNode)ImportTools.Offer(t.Tool, at.Count))]),
            AnswerAsync, ImportTools.MaxToolCalls, ct);
        if (run.Problem is { } p)
        {
            warnings.Add($"The plugins' tools could not be offered to the AI, so the draft is as it would be without them: {p.Message ?? "the AI did not answer"}");
            return;
        }
        // Last block first, so the numbers the model saw still hold; calls after the same block keep their order.
        foreach (var g in placed.Select((x, order) => (x, order)).GroupBy(x => (x.x.Page, x.x.Index)).OrderByDescending(g => g.Key.Page).ThenByDescending(g => g.Key.Index))
        {
            var content = contents[g.Key.Page];
            var at2 = g.Key.Index + 1;
            foreach (var n in g.OrderBy(x => x.order).SelectMany(x => x.x.Nodes)) content.Insert(at2++, n);
        }
        foreach (var o in outcomes.Where(o => o.Result == "done"))
            warnings.Add($"{contributors.PluginName(o.PluginId)} added {o.Nodes} block{(o.Nodes == 1 ? "" : "s")} through its tool {o.Tool}.");
        if (outcomes.Count(o => o.Result != "done") is > 0 and var refused)
            warnings.Add($"The AI's tool calls were refused {refused} time{(refused == 1 ? "" : "s")}: {string.Join(", ", outcomes.Where(o => o.Result != "done").Select(o => o.Result).Distinct())}.");
        if (run.Value!.LimitReached) warnings.Add($"The AI reached the limit of {ImportTools.MaxToolCalls} tool calls for this item.");
        log.LogInformation("Import tools for {Item}: {Calls} model call(s), {Tools} tool call(s), {In}+{Out} tokens, {Ms} ms.", item.Id, run.Value.ModelCalls, run.Value.ToolCalls,
            run.Value.PromptTokens, run.Value.CompletionTokens, run.Value.Milliseconds);
    }
}
