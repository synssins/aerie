using Kb.Plugins;

namespace Kb.Tests.Integration;

/// <summary>The drawing regions the tests declare for their tools, as a drawing plugin would; the app ships no drawing rule of its own.</summary>
internal static class TestRegions
{
    // The same patterns as the sample plugin's BoxesAndLines.Regions.
    public static ToolRegions Drawing { get; } = new(
        Starts: [@"[-=~_─━═]{3,}", @"\+[-=~_]|[-=~_]\+|[┌-╋]", @"\[\s+[^\]\[]+?\s+\]"],
        Joins: [@"[|+/\\]|[─-╿]"],
        Weak: [@"^\s*[-=~_*─━═]{3,}\s*$"],
        Alone:
        [
            @"\[\s+[^\]\[]+?\s+\]\s*<?(?:[-=~.]{2,}|-+[^\s\[\]]+-+)>?\s*\[\s+[^\]\[]+?\s+\]",
            @"(?:-{2,}|={2,})(?:[^\s<>\-=][^<>]*?-+)?>.*(?:-{2,}|={2,})(?:[^\s<>\-=][^<>]*?-+)?>",
        ]);
}
