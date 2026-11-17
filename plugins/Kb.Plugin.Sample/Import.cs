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
