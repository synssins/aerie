using System.Reflection;
using System.Text.RegularExpressions;
using Kb.Plugins;

namespace Kb.Tests.Unit;

/// <summary>The plugin guides stay true to the code: their sample code is the sample plugin's code, the manifest reference names every property, links lead somewhere.</summary>
public sealed partial class DocsTests
{
    private static readonly string Root = FindRoot();
    private static readonly string[] Guides = ["docs/plugins/developer-guide.md", "docs/plugins/manifest-reference.md", "docs/plugins/admin-guide.md"];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Kb.sln not found above the test folder.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative)).ReplaceLineEndings("\n");

    [GeneratedRegex(@"<!-- sample:(\w+) -->\n```(?:csharp|js)\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex SampleBlock();

    [GeneratedRegex(@"^[ \t]*// #region guide:(\w+)\n(.*?)^[ \t]*// #endregion", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex Region();

    [GeneratedRegex(@"\]\(([^)#\s]+)(#[^)\s]*)?\)")]
    private static partial Regex Link();

    /// <summary>The lines of a block without the indentation they all share.</summary>
    private static string Dedent(string text)
    {
        var lines = text.TrimEnd('\n').Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join('\n', lines.Select(l => l.Trim().Length == 0 ? "" : l[indent..]));
    }

    [Fact]
    public void Docs_SampleBlocks_MatchTheSample()
    {
        var sample = Path.Combine(Root, "plugins", "Kb.Plugin.Sample");
        var regions = Directory.GetFiles(sample, "*.cs").Concat(Directory.GetFiles(Path.Combine(sample, "ui"), "*.js"))
            .SelectMany(f => Region().Matches(File.ReadAllText(f).ReplaceLineEndings("\n")))
            .ToDictionary(m => m.Groups[1].Value, m => Dedent(m.Groups[2].Value));
        var blocks = SampleBlock().Matches(Read(Guides[0]));

        Assert.NotEmpty(blocks);
        foreach (Match block in blocks)
        {
            Assert.True(regions.TryGetValue(block.Groups[1].Value, out var code), $"The sample has no region guide:{block.Groups[1].Value}.");
            Assert.Equal(code, block.Groups[2].Value);
        }
        // Every region the sample marks for the guide is shown in it.
        Assert.Empty(regions.Keys.Except(blocks.Select(b => b.Groups[1].Value)));
    }

    [Fact]
    public void Docs_ManifestReference_ListsEveryManifestProperty()
    {
        var reference = Read(Guides[1]);
        Type[] types = [typeof(PluginManifest), typeof(PluginRequirement), typeof(PluginSetting), typeof(PluginPermission), typeof(PluginSchedule), typeof(PluginUi)];
        var names = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Where(p => p.Name != "EqualityContract")
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).Distinct().ToList();

        Assert.True(names.Count > 25, "the manifest types were not read");
        Assert.DoesNotContain(names, n => !reference.Contains($"| `{n}` |", StringComparison.Ordinal));
    }

    [Fact]
    public void Docs_LinkTargets_Exist()
    {
        var missing = new List<string>();
        foreach (var doc in Guides.Append("README.md"))
        {
            foreach (Match link in Link().Matches(Read(doc)))
            {
                var target = link.Groups[1].Value;
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(doc)!, target)))) missing.Add($"{doc}: {target}");
            }
        }
        Assert.Empty(missing);
    }
}
