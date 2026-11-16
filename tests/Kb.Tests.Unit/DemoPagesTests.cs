using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kb.Tests.Unit;

/// <summary>
/// tools/demo/whimsical-pages.json, the whimsical demo pages tools/Seed-DemoPages.ps1 writes. Each page is its own document:
/// its own symptoms, causes, steps and fixes, never another page's lines and never its title pasted into a sentence, and its
/// Applies to, Owner and Last verified each given on its own.
/// </summary>
public sealed partial class DemoPagesTests
{
    private static JsonElement Data()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tools", "demo", "whimsical-pages.json"))).RootElement;
    }

    private static IEnumerable<string> Lines(JsonElement page)
    {
        foreach (var b in page.GetProperty("blocks").EnumerateArray())
            foreach (var p in b.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                else if (p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var x in p.Value.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String) yield return x.GetString()!;
                        else foreach (var y in x.EnumerateArray()) if (y.ValueKind == JsonValueKind.String) yield return y.GetString()!;
            }
    }

    [Fact]
    public void EveryPage_HasItsTitleSpaceFolderAndItsOwnMetadata()
    {
        var pages = Data().GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(24, pages.Count);
        Assert.Equal(pages.Count, pages.Select(p => p.GetProperty("title").GetString()).Distinct().Count());
        Assert.Equal(4, pages.Select(p => p.GetProperty("space").GetString()).Distinct().Count());
        foreach (var p in pages)
            foreach (var field in new[] { "title", "space", "folder", "appliesTo", "owner", "lastVerified" })
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty(field).GetString()), $"{p.GetProperty("title")}: {field}");
    }

    [Fact]
    public void NoPage_PastesItsTitleIntoItsText()
    {
        foreach (var p in Data().GetProperty("pages").EnumerateArray())
        {
            var title = p.GetProperty("title").GetString()!;
            Assert.DoesNotContain(Lines(p), l => l.Contains(title, StringComparison.OrdinalIgnoreCase));
            // Nor its title without the verb where that is more than a subject ("the Kubernetes Kraken Back into Its Namespace").
            var tail = title[(title.IndexOf(' ', StringComparison.Ordinal) + 1)..];
            if (tail.Split(' ').Length > 4) Assert.DoesNotContain(Lines(p), l => l.Contains(tail, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NoTwoPages_ShareALine_OrTheSameSections()
    {
        var pages = Data().GetProperty("pages").EnumerateArray().ToList();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pages)
            foreach (var line in Lines(p).Where(l => l.Length > 30).Distinct())
                Assert.True(seen.TryAdd(Space().Replace(line, " ").Trim(), p.GetProperty("title").GetString()!),
                    $"\"{line}\" is in both {seen.GetValueOrDefault(line)} and {p.GetProperty("title")}");
        // Each page has sections, and they are not one set of sections for all.
        var sections = pages.Select(p => string.Join("|", p.GetProperty("blocks").EnumerateArray().Where(b => b.TryGetProperty("h", out _)).Select(b => b.GetProperty("h").GetString()))).ToList();
        Assert.All(sections, s => Assert.True(s.Split('|').Length >= 3, s));
        Assert.True(sections.Distinct().Count() >= 20, $"{sections.Distinct().Count()} different section lists");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
