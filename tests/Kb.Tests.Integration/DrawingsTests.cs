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
    [InlineData("|  CORE-SW1   |", true)]
    [InlineData("   [ laptops VLAN30 ]", true)]
    [InlineData("~~~~~~~~~~~~~~ AZURE", true)]
    [InlineData("┌──┐", true)]
    [InlineData("Restart the print spooler on print01.", false)]
    [InlineData("- a list item", false)]
    [InlineData("see [the guide](https://example.test) and [x] done", false)]
    [InlineData("=== notes/map.txt ===", false)]
    public void ALine_IsDrawn_ByItsRulesBarsCornersOrBrackets(string line, bool drawn) => Assert.Equal(drawn, Drawings.IsDrawn(line));

    [Fact]
    public void Drawings_AreFound_ApartFromTextAndTables()
    {
        var text = Hub + "\n\n| Port | Use |\n|------|-----|\n| 443  | web |\n| 22   | ssh |\n\nAnd a second one:\n\n[ a ] --- [ b ]\n  |\n[ c ] --- [ d ]\n";
        var found = Drawings.Find(text);
        Assert.Equal(2, found.Count); // the table is not one
        Assert.Contains(found[0].Lines, l => l.Contains("vnet-hub", StringComparison.Ordinal));
        Assert.Contains(found[1].Lines, l => l.Contains("[ c ]", StringComparison.Ordinal));
        Assert.Empty(Drawings.Find("Plain notes.\n1. Check the printer.\n2. Restart the spooler."));
        Assert.Empty(Drawings.Find("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |"));
        // A section break among lines with slashes and pluses (addresses, "1812 + 1813") is not a drawing.
        Assert.Empty(Drawings.Find("AADC01 -> internet: 443 tcp\n\n---\n\nWiFi -> DC02 on 1812 udp + 1813 udp.\n- app (10.50.8.0/22) -> SQL01 1433\n- SQL01 <-> SQL MI 5022"));
    }

    [Theory]
    [InlineData("vnet-prod 10.50.8.0/22", true)] // a name above an address in one box
    [InlineData("VNET-PROD   10.50.8.0/22", true)] // case and spaces aside
    [InlineData("vnet-prod 10.50.8.0/22 [SQL MI]", true)] // three lines of one box
    [InlineData("ExpressRoute (primary, BGP)", true)] // a line's label over two lines
    [InlineData("vnet-hub 10.50.0.0/22", true)] // on one line
    [InlineData("vnet-prod 10.50.16.0/22", false)] // the address of the next box
    [InlineData("vnet-prod vnet-avd", false)] // side by side, not stacked
    [InlineData("10.50.8.0/22 vnet-prod", false)] // out of order
    [InlineData("vnet-hub peering", false)] // from lines far apart
    [InlineData("vnet-prod 10.50.8 0/22", false)] // not as written
    public void ALabel_IsFoundOnOneLine_OrStackedInOneBox(string label, bool found) =>
        Assert.Equal(found, Drawings.Locate(label, Drawings.Find(Hub)) is not null);

    [Fact]
    public void AStack_HasItsBound()
    {
        var tall = Drawings.Find("+----+\n| a1 |\n| b2 |\n| c3 |\n| d4 |\n| e5 |\n+----+");
        Assert.NotNull(Drawings.Locate("a1 b2 c3 d4", tall)); // four parts
        Assert.Null(Drawings.Locate("a1 b2 c3 d4 e5", tall)); // five are too many
        var apart = Drawings.Find("+--------------+\n| alpha        |\n|        beta  |\n+--------------+");
        Assert.Null(Drawings.Locate("alpha beta", apart)); // not under one another
        Assert.Null(Drawings.Locate("vm", Drawings.Find("+------+\n| vmware |\n+------+"))); // not inside a longer word
    }
}
