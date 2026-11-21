using Kb.Plugins;
using Kb.Web.Import;

namespace Kb.Tests.Integration;

/// <summary>0.11.0: the drawings made of text characters that the app finds in an import's source, and labels written in them.</summary>
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
        Assert.Equal(drawn, TestRegions.Drawing.Starts.Any(p => System.Text.RegularExpressions.Regex.IsMatch(line, p)));

    [Fact]
    public void ATableOfBarsWithoutRules_IsNoDrawing() =>
        Assert.Empty(Regions.Find("Step | System | How |\n1 | BKP01 | normal shutdown |\n2 | PRT01 | normal shutdown |\n3 | AADC01 | normal shutdown |", TestRegions.Drawing));

    [Fact]
    public void TheAppsFileHeaders_AreNeverARegion() =>
        Assert.Empty(Regions.Find("=== notes/map.txt ===\n=== more.txt ===\n=== last.txt ===", TestRegions.Drawing));

    [Fact]
    public void Drawings_AreFound_ApartFromTextAndTables()
    {
        var text = Hub + "\n\n| Port | Use |\n|------|-----|\n| 443  | web |\n| 22   | ssh |\n\nAnd a second one:\n\n[ a ] --- [ b ]\n  |\n[ c ] --- [ d ]\n";
        var found = Regions.Find(text, TestRegions.Drawing);
        Assert.Equal(2, found.Count); // the table is not one
        Assert.Contains(found[0].Lines, l => l.Contains("vnet-hub", StringComparison.Ordinal));
        Assert.Contains(found[1].Lines, l => l.Contains("[ c ]", StringComparison.Ordinal));
        Assert.Empty(Regions.Find("Plain notes.\n1. Check the printer.\n2. Restart the spooler.", TestRegions.Drawing));
        Assert.Empty(Regions.Find("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |", TestRegions.Drawing));
        // A section break among lines with slashes and pluses (addresses, "1812 + 1813") is not a drawing.
        Assert.Empty(Regions.Find("AADC01 -> internet: 443 tcp\n\n---\n\nWiFi -> DC02 on 1812 udp + 1813 udp.\n- app (10.50.8.0/22) -> SQL01 1433\n- SQL01 <-> SQL MI 5022", TestRegions.Drawing));
    }

    [Theory]
    [InlineData("vnet-prod 10.50.8.0/22", true)] // a name above an address in one box
    [InlineData("VNET-PROD   10.50.8.0/22", true)] // case and spaces aside
    [InlineData("vnet-prod 10.50.8.0/22 [SQL MI]", true)] // three lines of one box
    [InlineData("vnet-prod / 10.50.8.0/22 / SQL MI", true)] // the line breaks given as separators, the brackets left out
    [InlineData("vnet-prod, SQL MI", false)] // a line left out between
    [InlineData("vnet-prod, 10.50.8.0/22, [SQL MI]", true)] // a comma at the end of a word is a separator
    [InlineData("ExpressRoute (primary, BGP)", true)] // a line's label over two lines
    [InlineData("vnet-hub 10.50.0.0/22", true)] // on one line
    [InlineData("vnet-prod 10.50.16.0/22", false)] // the address of the next box
    [InlineData("vnet-prod vnet-avd", false)] // side by side, not stacked
    [InlineData("10.50.8.0/22 vnet-prod", false)] // out of order
    [InlineData("vnet-hub peering", false)] // from lines far apart
    [InlineData("vnet-prod 10.50.8 0/22", false)] // not as written
    public void ALabel_IsFoundOnOneLine_OrStackedInOneBox(string label, bool found) =>
        Assert.Equal(found, Regions.Locate(label, Regions.Find(Hub, TestRegions.Drawing)) is not null);

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
    public void AShortDrawing_IsFound_AndProseIsNot(string text, int count) => Assert.Equal(count, Regions.Find(text, TestRegions.Drawing).Count);

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
    [InlineData("FS01 SQL01", "FS01 SQL01", null)] // a detail that repeats its label
    public void ABoxsDetail_IsWrittenInThatBox(string label, string detail, string? refused)
    {
        var input = new System.Text.Json.Nodes.JsonObject
        {
            ["boxes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "b1", ["label"] = label, ["detail"] = detail, ["shape"] = "box",
            }),
        };
        Assert.Equal(refused, ImportTools.Misplaced(input, BoxSchema, Regions.Find(Wiki, TestRegions.Drawing))?.Text);
    }

    private static readonly System.Text.Json.Nodes.JsonObject TitleSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{"boxes":{"type":"array","items":{"type":"object","properties":{
          "id":{"type":"string","format":"identifier"},"label":{"type":"string","format":"enclosed"},"title":{"type":"string","format":"beside"}}}}}}
        """)!.AsObject();

    [Theory]
    [InlineData("HV01", "FS01 SQL01", null)] // under the box
    [InlineData("vnet-prod", "10.50.8.0/22", null)] // in the same box: a title there is the person's to move
    [InlineData("FW01 (edge)", "SQL MI", "SQL MI")] // far from the box
    [InlineData("INTERNET", "Entra ID / M365 / Intune", "Entra ID / M365 / Intune", true)] // seen in round 11: at the end of a line from it
    public void ABoxsTitle_IsWrittenNearIt(string label, string title, string? refused, bool lineEnd = false)
    {
        Assert.Equal(lineEnd, ImportTools.AtLineEnd(title, Regions.Find(Wiki, TestRegions.Drawing)));
        var input = System.Text.Json.Nodes.JsonNode.Parse($$"""{"boxes":[{"id":"b1","label":"{{label}}","title":"{{title}}"}]}""");
        var found = ImportTools.Misplaced(input, TitleSchema, Regions.Find(Wiki, TestRegions.Drawing));
        Assert.Equal(refused, found?.Text);
        if (found is not null) Assert.True(found.Value.Beside);
    }

    [Theory]
    [InlineData("""{"groups":[{"id":"hub","label":"vnet-hub"}],"boxes":[{"id":"er","label":"ER GW","group":"hub"}]}""", "")]
    [InlineData("""{"groups":[],"boxes":[{"id":"er","label":"ER GW","group":"vnet_hub"},{"id":"dc","label":"DC01","group":"identity"},{"id":"x","label":"KV","group":"vnet_hub"}]}""", "vnet_hub,identity")] // seen in round 11
    public void AGroupNamed_IsDeclared(string json, string undeclared) =>
        Assert.Equal(undeclared, string.Join(",", ImportTools.Undeclared(System.Text.Json.Nodes.JsonNode.Parse(json), GroupSchema)));

    [Theory]
    [InlineData("HV01", "FS01 SQL01", true)] // under the box, outside it
    [InlineData("vnet-prod", "10.50.8.0/22", false)] // inside the same box: its detail, not a title
    [InlineData("FW01 (edge)", "SQL MI", false)] // in another box
    [InlineData("INTERNET", "Entra ID / M365 / Intune", false)] // at the end of a line
    public void AText_CouldBeATitle_OnlyBesideItsBox(string label, string text, bool could) =>
        Assert.Equal(could, ImportTools.CouldBeTitle(label, text, Regions.Find(Wiki, TestRegions.Drawing)));

    private static readonly System.Text.Json.Nodes.JsonObject DetailTitleSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{"boxes":{"type":"array","items":{"type":"object","properties":{
          "id":{"type":"string","format":"identifier"},"label":{"type":"string","format":"enclosed"},"detail":{"type":"string"},
          "title":{"type":"string","format":"beside"}}}}}}
        """)!.AsObject();

    [Theory]
    [InlineData("""{"boxes":[{"id":"b","label":"FW01 (edge)","title":"SQL MI"}]}""", "SQL MI")] // with its label: the title is far from it
    [InlineData("""{"boxes":[{"id":"b","detail":"FW01 (edge)","title":"SQL MI"}]}""", null)] // round 11, request 43: no label, so only the source check
    public void AnObjectWithNoLabel_HasNoLayoutChecks(string json, string? refused)
    {
        Assert.Equal("label", ImportTools.LabelKey(DetailTitleSchema["properties"]!["boxes"]!["items"]!["properties"]!.AsObject()));
        Assert.Equal(refused, ImportTools.Misplaced(System.Text.Json.Nodes.JsonNode.Parse(json), DetailTitleSchema, Regions.Find(Wiki, TestRegions.Drawing))?.Text);
    }

    [Theory]
    [InlineData("""{"groups":[{"id":"g"}],"boxes":[{"id":"m","label":"SQL MI","group":"g"}]}""")] // in a frame with no words: not checked against the layout
    [InlineData("""{"groups":[{"id":"g"}],"boxes":[{"id":"m","label":"HV01","group":"g"}]}""")]
    public void AGroupWithNoLabel_IsNotCheckedAgainstTheLayout(string json)
    {
        var input = System.Text.Json.Nodes.JsonNode.Parse(json);
        Assert.Null(ImportTools.NotInside(input, GroupSchema, Regions.Find(Wiki, TestRegions.Drawing)));
        Assert.Empty(ImportTools.Undeclared(input, GroupSchema));
    }

    [Theory]
    [InlineData("""{"groups":[{"id":"g","label":"vnet-prod"}],"boxes":[{"id":"m","label":"SQL MI","group":"g"}]}""", null)]
    [InlineData("""{"groups":[{"id":"g"}],"boxes":[{"id":"m","label":"SQL MI","group":"g"}]}""", null)] // a frame with no words
    [InlineData("""{"groups":[{"id":"g","label":"Lab notes, second floor"}],"boxes":[{"id":"m","label":"SQL MI","group":"g"}]}""", "Lab notes, second floor")] // seen in round 11: the page's heading
    public void AnItemsLabel_IsWrittenInTheDrawing(string json, string? refused)
    {
        var found = ImportTools.NotDrawn(System.Text.Json.Nodes.JsonNode.Parse(json), GroupSchema, Regions.Find(Wiki, TestRegions.Drawing));
        Assert.Equal(refused, found?.Label);
        if (found is not null) Assert.True(found.Value.Optional); // GroupSchema requires no label
    }

    private static readonly System.Text.Json.Nodes.JsonObject BoxesAndGroupsSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{
          "groups":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"identifier"},"label":{"type":"string"}}}},
          "boxes":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"identifier"},"label":{"type":"string","format":"enclosed"},
            "group":{"type":"string","format":"within"}}}}}}
        """)!.AsObject();

    [Theory]
    [InlineData("""{"groups":[{"id":"g","label":"vnet-prod"}],"boxes":[{"id":"m","label":"SQL MI","group":"g"}]}""", null)]
    [InlineData("""{"groups":[{"id":"g","label":"SQL MI"}],"boxes":[]}""", "SQL MI")] // seen in round 11: a bracketed item made a frame
    [InlineData("""{"groups":[{"id":"g","label":"[KV]"}],"boxes":[]}""", "[KV]")]
    public void AnItemInBrackets_IsABox(string json, string? refused)
    {
        var found = ImportTools.BracketedElsewhere(System.Text.Json.Nodes.JsonNode.Parse(json), BoxesAndGroupsSchema, Regions.Find(Wiki, TestRegions.Drawing));
        Assert.Equal(refused, found?.Label);
        if (found is not null) Assert.Equal("boxes", found.Value.Boxes);
        Assert.Null(ImportTools.BracketedElsewhere(System.Text.Json.Nodes.JsonNode.Parse(json), GroupSchema, Regions.Find(Wiki, TestRegions.Drawing))); // no box label marked
    }

    [Fact]
    public void TheFieldForATitle_IsNamedByItsFormat()
    {
        Assert.Equal("title", ImportTools.FormatKey(TitleSchema, "beside"));
        Assert.Null(ImportTools.FormatKey(BoxSchema, "beside"));
    }

    private static readonly System.Text.Json.Nodes.JsonObject GroupSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{
          "groups":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"identifier"},"label":{"type":"string"}}}},
          "boxes":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"identifier"},"label":{"type":"string"},
            "group":{"type":"string","format":"within"}}}}}}
        """)!.AsObject();

    [Theory]
    [InlineData("FS01 SQL01", "hv01", "FS01 SQL01")] // seen in round 8: bracketed items below a box, put in a group made of that box
    [InlineData("SQL MI", "prod", null)] // drawn inside the frame of its group
    [InlineData("10.50.8.0/22", "prod", null)] // text of the frame itself
    [InlineData("HV02", "prod", "HV02")] // a box elsewhere
    [InlineData("HV01", "bkp", "HV01")] // seen in round 9: a container in no box heads only what is written below it
    [InlineData("FS01 SQL01", "bkp", "FS01 SQL01")] // the line above it
    [InlineData("BKP01", "bkp", null)] // its own name's line
    [InlineData("FS01 SQL01", "nobody", null)] // a name nothing declares is left to the plugin
    public void AnItemInAGroup_IsDrawnInsideIt(string member, string group, string? refused)
    {
        var input = System.Text.Json.Nodes.JsonNode.Parse($$"""
            {"groups":[{"id":"prod","label":"vnet-prod"}],
             "boxes":[{"id":"hv01","label":"HV01"},{"id":"bkp","label":"BKP01"},{"id":"m","label":"{{member}}","group":"{{group}}"}]}
            """);
        Assert.Equal(refused, ImportTools.NotInside(input, GroupSchema, Regions.Find(Wiki, TestRegions.Drawing))?.Member);
    }

    [Theory]
    [InlineData("""{"groups":[{"id":"prod","label":"vnet-prod"}],"boxes":[{"id":"m","label":"SQL MI","group":"prod"}]}""", null)] // in its frame's group
    [InlineData("""{"groups":[],"boxes":[{"id":"v","label":"vnet-prod"},{"id":"m","label":"SQL MI"}]}""", "SQL MI")] // seen in round 10: beside its frame
    [InlineData("""{"groups":[],"boxes":[{"id":"h","label":"HV01"},{"id":"f","label":"FS01 SQL01"}]}""", null)] // below a box, not in it
    [InlineData("""{"groups":[],"boxes":[{"id":"p","label":"|"},{"id":"m","label":"SQL MI"}]}""", null)] // seen in round 11: a bar is no heading
    [InlineData("""{"groups":[],"boxes":[{"id":"a","label":"10.50.8.0/22"},{"id":"m","label":"SQL MI"}]}""", null)] // seen in round 10: a sibling below another line of the same box
    [InlineData("""{"groups":[{"id":"g","label":"vnet-prod"}],"boxes":[{"id":"v","label":"vnet-prod"},{"id":"m","label":"SQL MI","group":"g"}]}""", null)] // seen in round 10: the frame declared as a box and as a group
    public void AnItemDrawnInAFrame_IsInThatFramesGroup(string json, string? refused) =>
        Assert.Equal(refused, ImportTools.NotInside(System.Text.Json.Nodes.JsonNode.Parse(json), GroupSchema, Regions.Find(Wiki, TestRegions.Drawing))?.Member);

    [Fact]
    public void AHeading_ThatAlsoStartsTextInAFrame_IsNotPutInThatFrame()
    {
        // Seen in round 10: "AZURE" also starts "[Azure Firewall]" in vnet-x's frame, and was told to sit in vnet-x.
        const string map = """
            ~~~~~~~~~~~~~~~~~~~~~~~~ AZURE
            +----------------------+
            | vnet-x               |
            | [Azure Firewall]     |
            +----------------------+
            """;
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{
              "groups":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"identifier"},"label":{"type":"string"},"group":{"type":"string","format":"within"}}}},
              "boxes":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"identifier"},"label":{"type":"string"},"group":{"type":"string","format":"within"}}}}}}
            """)!.AsObject();
        var input = System.Text.Json.Nodes.JsonNode.Parse("""
            {"groups":[{"id":"az","label":"AZURE"},{"id":"vx","label":"vnet-x","group":"az"}],"boxes":[{"id":"f","label":"Azure Firewall","group":"vx"}]}
            """);
        Assert.Null(ImportTools.NotInside(input, schema, Regions.Find(map, TestRegions.Drawing)));
    }

    [Fact]
    public void AGroup_WrittenAlsoWhereNoBoxHoldsIt_IsNotChecked()
    {
        // Seen in round 9: "AZURE" heads a line of tildes, and also starts "[Azure Firewall]" in a box; its members are not refused.
        const string map = """
            +--------------------+
            | [Azure Firewall]   |
            +--------------------+
            ~~~~~~~~~~~~~~~~~~~~~~ AZURE
            +----------+
            | vnet-a   |
            +----------+
            """;
        // And "AZURE" itself is not taken to be drawn inside the box of "[Azure Firewall]" (seen in round 10).
        var input = System.Text.Json.Nodes.JsonNode.Parse("""{"groups":[{"id":"az","label":"AZURE"}],"boxes":[{"id":"a","label":"vnet-a","group":"az"},{"id":"f","label":"Azure Firewall"}]}""");
        Assert.Null(ImportTools.NotInside(input, GroupSchema, Regions.Find(map, TestRegions.Drawing)));
    }

    private static readonly System.Text.Json.Nodes.JsonObject EnclosedSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{"boxes":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string","format":"enclosed"}}}}}}
        """)!.AsObject();

    [Theory]
    [InlineData("HV01", null)] // in its box
    [InlineData("FS01 SQL01", null)] // between brackets
    [InlineData("Entra ID / M365 / Intune", null)] // seen in round 10: the end of a line drawn across ("|---- Entra ID"), a box of its own
    public void ABoxsLabel_StandsInABox(string label, string? refused)
    {
        var input = System.Text.Json.Nodes.JsonNode.Parse($$"""{"boxes":[{"label":"{{label}}"}]}""");
        Assert.Equal(refused, ImportTools.NotEnclosed(input, EnclosedSchema, Regions.Find(Wiki, TestRegions.Drawing)));
    }

    [Fact]
    public void ALinesLabel_BesideALine_IsNoBox() =>
        Assert.Equal("ExpressRoute", ImportTools.NotEnclosed(System.Text.Json.Nodes.JsonNode.Parse("""{"boxes":[{"label":"ExpressRoute"}]}"""), EnclosedSchema,
            Regions.Find(Hub, TestRegions.Drawing)));

    [Theory]
    [InlineData("vnet-prod 10.50.8.0/22 SQL MI KV", "vnet-prod 10.50.8.0/22 SQL MI KV")] // seen in round 10: a frame's label with its items
    [InlineData("FS01 SQL01 BKP01", "FS01 SQL01 BKP01")] // two items in one
    [InlineData("[SQL MI]", null)] // one item, as written
    [InlineData("SQL MI", null)] // one item, its brackets left out
    [InlineData("vnet-prod", null)] // no item at all
    public void ABoxsLabel_DoesNotJoinBracketedItems(string label, string? refused) =>
        Assert.Equal(refused, ImportTools.Joins(System.Text.Json.Nodes.JsonNode.Parse($$"""{"boxes":[{"label":"{{label}}"}]}"""), EnclosedSchema,
            Regions.Find(Wiki, TestRegions.Drawing)));

    [Fact]
    public void ABoxsLabel_InAChainOfBareNames_IsNotChecked() =>
        Assert.Null(ImportTools.NotEnclosed(System.Text.Json.Nodes.JsonNode.Parse("""{"boxes":[{"label":"FW01"}]}"""), EnclosedSchema,
            Regions.Find("printer --587--> FW01 --> Exchange Online", TestRegions.Drawing)));

    [Fact]
    public void AStack_HasItsBound()
    {
        // Inside a box, the box is the bound: all its lines, and no line across its rule.
        var tall = Regions.Find("+----+\n| a1 |\n| b2 |\n| c3 |\n| d4 |\n| e5 |\n+----+\n| f6 |\n+----+", TestRegions.Drawing);
        Assert.NotNull(Regions.Locate("a1 b2 c3 d4 e5", tall, out var fallback)); // five parts in one box
        Assert.False(fallback);
        Assert.Null(Regions.Locate("d4 e5 f6", tall)); // across the rule between two boxes
        // Outside any box (a line's label), at most four lines, and the fallback says so.
        var loose = Regions.Find("+--+\n|  |\n+--+\n |  a1\n |  b2\n |  c3\n |  d4\n |  e5\n+--+\n|  |\n+--+", TestRegions.Drawing);
        Assert.NotNull(Regions.Locate("a1 b2 c3 d4", loose, out fallback)); // four parts
        Assert.True(fallback);
        Assert.Null(Regions.Locate("a1 b2 c3 d4 e5", loose)); // five are too many
        Assert.NotNull(Regions.Locate("ExpressRoute (primary, BGP)", Regions.Find(Hub, TestRegions.Drawing), out fallback));
        Assert.True(fallback);
        Assert.NotNull(Regions.Locate("vnet-prod 10.50.8.0/22", Regions.Find(Hub, TestRegions.Drawing), out fallback));
        Assert.False(fallback);
        var apart = Regions.Find("+--------------+\n| alpha        |\n|        beta  |\n+--------------+", TestRegions.Drawing);
        Assert.Null(Regions.Locate("alpha beta", apart)); // not under one another
        Assert.Null(Regions.Locate("vm", Regions.Find("+------+\n| vmware |\n+------+", TestRegions.Drawing))); // not inside a longer word
    }
}
