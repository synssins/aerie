using Kb.Plugins;
using Kb.Web.Import;

namespace Kb.Tests.Integration;

/// <summary>0.11.0 (draft): the drawings made of text characters that the app finds in an import's source, and labels written in them.</summary>
public sealed class DrawingsTests
{
    private const string Hub = """
        Notes before.

            +-------+----------+--------+
            |  vnet-hub 10.50.0.0/22    |
            +------+-------------+------+
                   |             |   peering
        +----------+--+     +----+----------------+
        | vnet-prod   |     | vnet-avd            |
        | 10.50.8.0/22|     | 10.50.16.0/22       |
        | [SQL MI]    |     | [AVD host pool]     |
        +-------------+     +---------------------+
                   ExpressRoute     |
                   (primary, BGP)   |

        Notes after.
        """;

    [Theory]
    [InlineData("+-------+--------+", true)]
    [InlineData("|  CORE-SW1   |", false)] // a line of bars joins a drawing, but does not start one
    [InlineData("   [ laptops VLAN30 ]", true)]
    [InlineData("~~~~~~~~~~~~~~ AZURE", true)]
    [InlineData("┌──┐", true)]
    [InlineData("Restart the print spooler on print01.", false)]
    [InlineData("- a list item", false)]
    [InlineData("see [the guide](https://example.test) and [x] done", false)]
    public void ALine_StartsADrawing_ByItsRulesBarsCornersOrBrackets(string line, bool drawn) =>
        Assert.Equal(drawn, ToolRegions.TextDrawing.Starts.Any(p => System.Text.RegularExpressions.Regex.IsMatch(line, p)));

    [Fact]
    public void ATableOfBarsWithoutRules_IsNoDrawing() =>
        Assert.Empty(Regions.Find("Step | System | How |\n1 | BKP01 | normal shutdown |\n2 | PRT01 | normal shutdown |\n3 | AADC01 | normal shutdown |", ToolRegions.TextDrawing));

    [Fact]
    public void TheAppsFileHeaders_AreNeverARegion() =>
        Assert.Empty(Regions.Find("=== notes/map.txt ===\n=== more.txt ===\n=== last.txt ===", ToolRegions.TextDrawing));

    [Fact]
    public void Drawings_AreFound_ApartFromTextAndTables()
    {
        var text = Hub + "\n\n| Port | Use |\n|------|-----|\n| 443  | web |\n| 22   | ssh |\n\nAnd a second one:\n\n[ a ] --- [ b ]\n  |\n[ c ] --- [ d ]\n";
        var found = Regions.Find(text, ToolRegions.TextDrawing);
        Assert.Equal(2, found.Count); // the table is not one
        Assert.Contains(found[0].Lines, l => l.Contains("vnet-hub", StringComparison.Ordinal));
        Assert.Contains(found[1].Lines, l => l.Contains("[ c ]", StringComparison.Ordinal));
        Assert.Empty(Regions.Find("Plain notes.\n1. Check the printer.\n2. Restart the spooler.", ToolRegions.TextDrawing));
        Assert.Empty(Regions.Find("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |", ToolRegions.TextDrawing));
        // A section break among lines with slashes and pluses (addresses, "1812 + 1813") is not a drawing.
        Assert.Empty(Regions.Find("AADC01 -> internet: 443 tcp\n\n---\n\nWiFi -> DC02 on 1812 udp + 1813 udp.\n- app (10.50.8.0/22) -> SQL01 1433\n- SQL01 <-> SQL MI 5022", ToolRegions.TextDrawing));
    }

    [Theory]
    [InlineData("vnet-prod 10.50.8.0/22", true)] // a name above an address in one box
    [InlineData("VNET-PROD   10.50.8.0/22", true)] // case and spaces aside
    [InlineData("vnet-prod 10.50.8.0/22 [SQL MI]", true)] // three lines of one box
    [InlineData("vnet-prod / 10.50.8.0/22 / SQL MI", true)] // the line breaks given as separators, the brackets left out
    [InlineData("vnet-prod, SQL MI", false)] // a line left out between
    [InlineData("ExpressRoute (primary, BGP)", true)] // a line's label over two lines
    [InlineData("vnet-hub 10.50.0.0/22", true)] // on one line
    [InlineData("vnet-prod 10.50.16.0/22", false)] // the address of the next box
    [InlineData("vnet-prod vnet-avd", false)] // side by side, not stacked
    [InlineData("10.50.8.0/22 vnet-prod", false)] // out of order
    [InlineData("vnet-hub peering", false)] // from lines far apart
    [InlineData("vnet-prod 10.50.8 0/22", false)] // not as written
    public void ALabel_IsFoundOnOneLine_OrStackedInOneBox(string label, bool found) =>
        Assert.Equal(found, Regions.Locate(label, Regions.Find(Hub, ToolRegions.TextDrawing)) is not null);

    [Theory]
    [InlineData("[ A ] --- [ B ] --> [ C ]", 1)] // one line
    [InlineData("Lab notes\n\n   [ web01 ] --- [ db01 ]\n\nNotes after.", 1)]
    [InlineData("[ printer ] --587--> [ FW01 ]\n                (smtp relay)", 1)] // two lines, a label in the connector
    [InlineData("[ web ] <==> [ app ]", 1)]
    [InlineData("Use A --> B, then restart.", 0)] // prose with an arrow
    [InlineData("AADC01 -> internet: 443 tcp\nWiFi -> DC02 on 1812 udp", 0)]
    [InlineData("- [ ] check the printer\n- [x] restart the spooler -- done", 0)] // a task list
    [InlineData("See [the guide](https://example.test) -- or [the wiki](https://wiki.test).", 0)] // links
    [InlineData("| a | b |\n|---|---|\n| [ x ] -- [ y ] | 2 |", 0)] // a table
    [InlineData("Ask [ IT ] first.", 0)] // one thing in brackets
    public void AShortDrawing_IsFound_AndProseIsNot(string text, int count) => Assert.Equal(count, Regions.Find(text, ToolRegions.TextDrawing).Count);

    private const string Wiki = """
           +--+--------+-------+--+
              |        |       |
        +-----+--+  +--+---+  +---+----------+
        | HV01   |  | HV02 |  |  FW01 (edge) |
        +--+-----+  +--+---+  +---+------+---+
           |                      |      |
         [ FS01 SQL01 ]           |      |
         [ BKP01      ]           |      |
                              +---+----+ +---+--------+
                              | ER     | |  INTERNET  |---- Entra ID / M365 / Intune
                              +--------+ +---+--------+
        +--------------------+
        |  vnet-prod         |
        |  10.50.8.0/22      |
        |  [SQL MI] [KV]     |
        +--------------------+
        """;

    private static readonly System.Text.Json.Nodes.JsonObject BoxSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{"boxes":{"type":"array","items":{"type":"object","properties":{
          "id":{"type":"string","format":"identifier"},"label":{"type":"string"},"detail":{"type":"string"},
          "shape":{"type":"string","enum":["box","note"]}}}}}}
        """)!.AsObject();

    [Theory]
    [InlineData("INTERNET", "Entra ID / M365 / Intune", "Entra ID / M365 / Intune")] // round 6: written outside the box, beside a line from it
    [InlineData("FW01 (edge)", "BKP01", "BKP01")] // round 6: written elsewhere in the drawing
    [InlineData("HV01", "FS01 SQL01", "FS01 SQL01")] // a thing in brackets below the box, not in it
    [InlineData("vnet-prod", "10.50.8.0/22 [SQL MI] [KV]", null)] // in the same box
    [InlineData("vnet-prod\n10.50.8.0/22", "", null)] // the label's second line, in the same box
    [InlineData("FS01 SQL01", "BKP01", null)] // in no box: stacked under it
    [InlineData("FS01 SQL01", "Entra ID / M365 / Intune", "Entra ID / M365 / Intune")] // in no box, and not under it
    [InlineData("Lab notes", "anything at all", null)] // a label in no drawing is left to the source check
    public void ABoxsDetail_IsWrittenInThatBox(string label, string detail, string? refused)
    {
        var input = new System.Text.Json.Nodes.JsonObject
        {
            ["boxes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "b1", ["label"] = label, ["detail"] = detail, ["shape"] = "box",
            }),
        };
        Assert.Equal(refused, ImportTools.Misplaced(input, BoxSchema, Regions.Find(Wiki, ToolRegions.TextDrawing))?.Text);
    }

    [Fact]
    public void AStack_HasItsBound()
    {
        // Inside a box, the box is the bound: all its lines, and no line across its rule.
        var tall = Regions.Find("+----+\n| a1 |\n| b2 |\n| c3 |\n| d4 |\n| e5 |\n+----+\n| f6 |\n+----+", ToolRegions.TextDrawing);
        Assert.NotNull(Regions.Locate("a1 b2 c3 d4 e5", tall, out var fallback)); // five parts in one box
        Assert.False(fallback);
        Assert.Null(Regions.Locate("d4 e5 f6", tall)); // across the rule between two boxes
        // Outside any box (a line's label), at most four lines, and the fallback says so.
        var loose = Regions.Find("+--+\n|  |\n+--+\n |  a1\n |  b2\n |  c3\n |  d4\n |  e5\n+--+\n|  |\n+--+", ToolRegions.TextDrawing);
        Assert.NotNull(Regions.Locate("a1 b2 c3 d4", loose, out fallback)); // four parts
        Assert.True(fallback);
        Assert.Null(Regions.Locate("a1 b2 c3 d4 e5", loose)); // five are too many
        Assert.NotNull(Regions.Locate("ExpressRoute (primary, BGP)", Regions.Find(Hub, ToolRegions.TextDrawing), out fallback));
        Assert.True(fallback);
        Assert.NotNull(Regions.Locate("vnet-prod 10.50.8.0/22", Regions.Find(Hub, ToolRegions.TextDrawing), out fallback));
        Assert.False(fallback);
        var apart = Regions.Find("+--------------+\n| alpha        |\n|        beta  |\n+--------------+", ToolRegions.TextDrawing);
        Assert.Null(Regions.Locate("alpha beta", apart)); // not under one another
        Assert.Null(Regions.Locate("vm", Regions.Find("+------+\n| vmware |\n+------+", ToolRegions.TextDrawing))); // not inside a longer word
    }
}
