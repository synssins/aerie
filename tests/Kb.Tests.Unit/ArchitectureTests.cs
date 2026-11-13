using System.Text.RegularExpressions;

namespace Kb.Tests.Unit;

/// <summary>Rules the source must keep, read from the files: where routes, handlers and page queries may be, and what they may use.</summary>
public sealed partial class ArchitectureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Kb.sln not found above the test folder.");
    }

    /// <summary>Every line of every C# file in a folder of src/Kb.Web, as "file:line: text". The folder must exist and hold code.</summary>
    private static List<string> Lines(string folder)
    {
        var path = Path.Combine(Root, "src", "Kb.Web", folder);
        Assert.True(Directory.Exists(path), $"src/Kb.Web/{folder} does not exist.");
        var lines = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((text, i) => $"{Path.GetFileName(f)}:{i + 1}: {text}")).ToList();
        Assert.NotEmpty(lines);
        return lines;
    }

    [GeneratedRegex(@"\b(KbDbContext|IAuditLog|SaveChanges\w*|ISpacePermissionService|IAccess)\b")]
    private static partial Regex DatabaseOrPermission();

    [GeneratedRegex(@"\.Pages\b(?!\.Add)")]
    private static partial Regex PageQuery();

    /// <summary>A route binds the request, sends one action and answers. The work and the checks are the handler's.</summary>
    [Fact]
    public void Routes_DoNotTouchTheDatabase() => Assert.DoesNotContain(Lines("Routes"), l => DatabaseOrPermission().IsMatch(l));

    /// <summary>Handlers ask IAccess, which knows about API tokens and plugins as actors. The service behind it is not for them.</summary>
    [Fact]
    public void Handlers_UseTheAccessFront() => Assert.DoesNotContain(Lines("Actions"), l => l.Contains("ISpacePermissionService", StringComparison.Ordinal));

    /// <summary>
    /// A handler reaches pages for its caller only through a scope (In, FindVisibleAsync). A query for the system's own bookkeeping says so on its line.
    /// </summary>
    [Fact]
    public void Handlers_ReachPagesThroughScope() => Assert.DoesNotContain(Lines("Actions"), l =>
        PageQuery().IsMatch(l) && !l.Contains(".In(", StringComparison.Ordinal) && !l.Contains("FindVisibleAsync", StringComparison.Ordinal) && !l.Contains("// system:", StringComparison.Ordinal));
}
