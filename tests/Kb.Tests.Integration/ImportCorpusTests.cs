using System.Text.RegularExpressions;
using Kb.Core.Import;
using Kb.Web.Import;

namespace Kb.Tests.Integration;

/// <summary>
/// Reads a local test corpus (KB_CORPUS: a folder with _answer-key/RELATED-GROUPS.md) without the model: every file must read
/// without throwing, and the grouping by change, incident and case numbers is scored against the answer key.
/// Without KB_CORPUS it does nothing; the corpus is not part of the repository.
/// </summary>
public sealed partial class ImportCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void Corpus_Reads_AndGroupsLikeTheAnswerKey()
    {
        var root = Environment.GetEnvironmentVariable("KB_CORPUS");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        var files = new List<ImportFile>();
        var top = new Dictionary<Guid, string>();
        var kinds = new Dictionary<string, int>();
        void Read(string path, byte[] data, Guid? parent, string topPath, int depth)
        {
            var f = new ImportFile { Path = path, Name = Path.GetFileName(path), Sha256 = "x", Kind = "Pending", ParentId = parent };
            var x = Extractors.Extract(path, data);
            (f.Kind, f.Text, f.Note) = (x.Kind.ToString(), x.Text.Length > 0 ? x.Text : null, x.Note);
            files.Add(f);
            top[f.Id] = topPath;
            var key = $"{Path.GetExtension(path).ToLowerInvariant()} {x.Kind}";
            kinds[key] = kinds.GetValueOrDefault(key) + 1;
            if (depth < Extractors.MaxDepth)
                foreach (var c in x.Children) Read($"{path}/{c.Path}", c.Data, f.Id, topPath, depth + 1);
        }
        foreach (var p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, p).Replace('\\', '/');
            if (rel.StartsWith('_') || rel.StartsWith("MANIFEST", StringComparison.Ordinal)) continue;
            Read(rel, File.ReadAllBytes(p), null, rel, 0);
        }
        foreach (var k in kinds.OrderBy(k => k.Key, StringComparer.Ordinal)) output.WriteLine($"{k.Key}: {k.Value}");

        var groups = ImportService.ByIdentifiers(ImportService.Group(files), files);
        var predicted = groups.Select(g => g.Select(id => top[id]).ToHashSet()).ToList();
        var key = File.ReadAllText(Path.Combine(root, "_answer-key", "RELATED-GROUPS.md"));
        int whole = 0, total = 0;
        foreach (Match g in GroupHeading().Matches(key))
        {
            var members = FileLine().Matches(g.Groups[2].Value).Select(m => m.Groups[1].Value.Trim()).ToHashSet();
            total++;
            var hit = predicted.Where(p => p.Overlaps(members)).ToList();
            var best = hit.OrderByDescending(p => p.Count(members.Contains)).FirstOrDefault() ?? [];
            var found = best.Count(members.Contains);
            var extra = best.Count(p => !members.Contains(p));
            if (found == members.Count && extra == 0) whole++;
            output.WriteLine($"{g.Groups[1].Value}: {found}/{members.Count} in one group, {extra} extra, split over {hit.Count}");
            if (Environment.GetEnvironmentVariable("KB_CORPUS_DETAIL") is not { Length: > 0 }) continue;
            foreach (var m in members.Where(m => !best.Contains(m)))
                output.WriteLine($"   missed {m}: {string.Join(' ', files.Where(f => top[f.Id] == m).Select(f => $"[{f.Kind} {Ids(f.Text)}]"))}");
            foreach (var e in best.Where(p => !members.Contains(p)).Take(5))
                output.WriteLine($"   extra {e}: {string.Join(' ', files.Where(f => top[f.Id] == e).Select(f => $"[{f.Kind} {Ids(f.Text)}]")).Truncate(300)}");
        }
        output.WriteLine($"Whole groups: {whole}/{total}. Predicted groups: {predicted.Count}. Files read: {files.Count}.");
    }

    private static string Ids(string? text) =>
        text is null ? "-" : string.Join(',', Regex.Matches(text, @"\b(?:CHG|INC)\d{5,}\b|\b[A-Z]{2,4}-\d{5,8}\b").Select(m => m.Value).Distinct());

    [GeneratedRegex(@"^## (G\d+)[^\n]*\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex GroupHeading();

    [GeneratedRegex(@"^- ((?:clean|messy|bundles|zips|edge-cases|images|onenote)/[^\n]+)$", RegexOptions.Multiline)]
    private static partial Regex FileLine();
}
