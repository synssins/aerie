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
