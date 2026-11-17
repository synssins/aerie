using System.Text.Json;
using Kb.Plugins;

namespace Kb.Plugin.Sample;

// #region guide:import
/// <summary>
/// Adds to an AI import's draft: a file named "*.notice.txt" becomes a notice at the end of the page drafted from it.
/// It gets the file only after every virus scanner passed it, as a read-only stream, and answers editor nodes as a page stores them.
/// </summary>
public sealed class NoticeFromFile : IImportContributor
{
    public async Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct)
    {
        if (!file.FileName.EndsWith(".notice.txt", StringComparison.OrdinalIgnoreCase) || file.Length > 10_000) return null;
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
