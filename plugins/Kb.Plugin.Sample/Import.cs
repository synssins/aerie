using System.Text.Json;
using Kb.Plugins;

namespace Kb.Plugin.Sample;

// #region guide:import
/// <summary>
/// Adds to an AI import's draft: a file named "*.notice.txt" becomes a notice at the end of the page drafted from it. A "*.notice"
/// file is a kind the app does not read; the plugin claims it (<c>ImportFileKinds(".notice")</c>), so its page is drafted by this alone.
/// It gets the file only after every virus scanner passed it, as a read-only stream, and answers editor nodes as a page stores them.
/// </summary>
public sealed class NoticeFromFile : IImportContributor
{
    public async Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct)
    {
        if (!(file.FileName.EndsWith(".notice.txt", StringComparison.OrdinalIgnoreCase) || file.FileName.EndsWith(".notice", StringComparison.OrdinalIgnoreCase))
            || file.Length > 10_000) return null;
        await using var stream = await file.OpenReadAsync(ct);
        var line = (await new StreamReader(stream).ReadLineAsync(ct))?.Trim();
        if (string.IsNullOrEmpty(line)) return null;
        var node = JsonSerializer.SerializeToElement(new { type = "sampleNotice", content = new[] { new { type = "text", text = line } } });
        return new ImportContribution([node]);
    }
}
// #endregion

// #region guide:draft
/// <summary>
/// Adds to an AI import's draft from its text: a paragraph that starts "Notice:" in a drafted page is repeated as a notice near the top
/// of that page, after its introduction, so the importer reviews it with the rest. It reads the draft and answers where its nodes go.
/// </summary>
public sealed class NoticeFromDraft : IDraftContributor
{
    public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct)
    {
        var additions = new List<DraftAddition>();
        for (var i = 0; i < draft.Pages.Count; i++)
        {
            using var doc = JsonDocument.Parse(draft.Pages[i].ContentJson);
            var blocks = doc.RootElement.TryGetProperty("content", out var c) ? c.EnumerateArray().ToList() : [];
            if (blocks.Any(b => b.GetProperty("type").GetString() == "sampleNotice")) continue; // a file already gave this page its notice
            var notice = blocks.Where(b => b.GetProperty("type").GetString() == "paragraph" && b.TryGetProperty("content", out _))
                .Select(b => string.Concat(b.GetProperty("content").EnumerateArray().Select(t => t.TryGetProperty("text", out var x) ? x.GetString() : "")))
                .FirstOrDefault(t => t.StartsWith("Notice:", StringComparison.Ordinal))?[7..].Trim();
            if (string.IsNullOrEmpty(notice)) continue;
            var node = JsonSerializer.SerializeToElement(new { type = "sampleNotice", content = new[] { new { type = "text", text = notice } } });
            additions.Add(new DraftAddition(i, DraftPlace.AfterIntroduction, [node]));
        }
        return Task.FromResult(additions.Count == 0 ? null : new DraftContribution(additions));
    }
}
// #endregion

// #region guide:diagram
/// <summary>
/// Reads the diagrams the AI found in an import's pictures (0.10.0) and puts an outline of each right after its picture: one bullet per
/// container with what it holds, then one per line ("A to B: label"). A chart plugin would draw the same data instead.
/// </summary>
public sealed class DiagramOutline : IDraftContributor
{
    public Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct)
    {
        var additions = new List<DraftAddition>();
        foreach (var picture in draft.Pictures)
        {
            if (picture.Diagram is not { } d) continue;
            var names = d.Boxes.ToDictionary(b => b.Id, b => b.Label);
            foreach (var c in d.Containers) names[c.Id] = c.Label;
            var items = new List<string>();
            foreach (var c in d.Containers)
                items.Add($"{c.Label}: {string.Join(", ", d.Boxes.Where(b => b.Container == c.Id).Select(b => b.Label).Concat(d.Containers.Where(x => x.Container == c.Id).Select(x => x.Label)))}");
            foreach (var l in d.Lines)
                items.Add($"{names[l.From]} to {names[l.To]}{(l.Label is { } label ? $": {label}" : "")}{(l.Dashed ? " (dashed)" : "")}");
            if (items.Count == 0) continue;
            var list = JsonSerializer.SerializeToElement(new
            {
                type = "bulletList",
                content = items.Select(t => new { type = "listItem", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = t } } } } }),
            });
            additions.Add(new DraftAddition(picture.Page, DraftPlace.AfterPicture, [list]) { Picture = picture.Src });
        }
        return Task.FromResult(additions.Count == 0 ? null : new DraftContribution(additions));
    }
}
// #endregion

// #region guide:tool
/// <summary>
/// A tool the AI may call while it drafts an import (0.11.0 draft): a drawing of boxes, groups and lines in a document (a network map
/// drawn in text) becomes an outline after the block that shows it. The app checks the input against <see cref="Schema"/> before the
/// call, so the plugin gets only valid input; it never sees the document. A chart plugin would draw the same data instead.
/// </summary>
public sealed class BoxesAndLines : IImportTool
{
    public const string Name = "boxes_and_lines";
    public const string Description = "Draw a diagram of boxes, groups of boxes and lines between them, from a drawing in the documents such as a network map drawn in text.";
    public const string Schema = """
        {"type": "object", "required": ["boxes", "lines"], "additionalProperties": false, "properties": {
          "title": {"type": "string", "maxLength": 100},
          "groups": {"type": "array", "maxItems": 40, "items": {"type": "object", "required": ["id", "label"], "additionalProperties": false, "properties": {
            "id": {"type": "string", "maxLength": 32}, "label": {"type": "string", "maxLength": 100},
            "group": {"type": "string", "maxLength": 32, "description": "The id of the group it sits in, if any."}}}},
          "boxes": {"type": "array", "minItems": 1, "maxItems": 150, "items": {"type": "object", "required": ["id", "label"], "additionalProperties": false, "properties": {
            "id": {"type": "string", "maxLength": 32}, "label": {"type": "string", "maxLength": 100},
            "group": {"type": "string", "maxLength": 32, "description": "The id of the group it sits in, if any."}}}},
          "lines": {"type": "array", "maxItems": 200, "items": {"type": "object", "required": ["from", "to"], "additionalProperties": false, "properties": {
            "from": {"type": "string", "maxLength": 32}, "to": {"type": "string", "maxLength": 32}, "label": {"type": "string", "maxLength": 100}}}}}}
        """;

    public Task<ImportToolResult?> CallAsync(JsonElement input, CancellationToken ct)
    {
        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var groups = input.TryGetProperty("groups", out var g) ? g.EnumerateArray().ToList() : [];
        var boxes = input.GetProperty("boxes").EnumerateArray().ToList();
        var names = groups.Concat(boxes).GroupBy(x => Text(x, "id")).ToDictionary(x => x.Key, x => Text(x.First(), "label"));
        object Item(string text, IEnumerable<object> children)
        {
            var list = children.ToList();
            var para = new { type = "paragraph", content = new[] { new { type = "text", text } } };
            return list.Count == 0 ? new { type = "listItem", content = new object[] { para } } : new { type = "listItem", content = new object[] { para, new { type = "bulletList", content = list } } };
        }
        // What sits in a group (or at the top), groups first. A group that names itself, or a loop, is shown once at the top.
        var seen = new HashSet<string>();
        IEnumerable<object> In(string group) =>
            groups.Where(x => Text(x, "group") == group && seen.Add(Text(x, "id"))).Select(x => Item(Text(x, "label"), In(Text(x, "id"))))
                .Concat(boxes.Where(x => Text(x, "group") == group).Select(x => Item(Text(x, "label"), [])));
        var top = In("").ToList();
        var lines = input.GetProperty("lines").EnumerateArray().Select(l => Item(
            $"{names.GetValueOrDefault(Text(l, "from"), Text(l, "from"))} to {names.GetValueOrDefault(Text(l, "to"), Text(l, "to"))}{(Text(l, "label") is { Length: > 0 } x ? ": " + x : "")}", [])).ToList();
        var nodes = new List<JsonElement>();
        if (Text(input, "title") is { Length: > 0 } title)
            nodes.Add(JsonSerializer.SerializeToElement(new { type = "paragraph", content = new[] { new { type = "text", text = title, marks = new[] { new { type = "bold" } } } } }));
        nodes.Add(JsonSerializer.SerializeToElement(new { type = "bulletList", content = top }));
        if (lines.Count > 0) nodes.Add(JsonSerializer.SerializeToElement(new { type = "bulletList", content = lines }));
        return Task.FromResult<ImportToolResult?>(new ImportToolResult(nodes));
    }
}
// #endregion
