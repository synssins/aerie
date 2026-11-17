using System.Text.Json;
using System.Text.Json.Nodes;
using Kb.Plugins;
using Kb.Web.Import;

namespace Kb.Tests.Integration;

/// <summary>Where a draft contributor's nodes go, and how its answer is checked. No app is started.</summary>
public sealed class DraftPlacementTests
{
    private static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonElement Node(string type) => JsonDocument.Parse($$"""{"type":"{{type}}"}""").RootElement.Clone();

    private static JsonArray Page(params string[] types) => [.. types.Select(t => (JsonNode)new JsonObject { ["type"] = t })];

    private static List<string> Types(JsonArray page) => [.. page.Select(n => n!["type"]!.GetValue<string>())];

    private static void Place(JsonArray page, params DraftAddition[] additions) =>
        ImportContributors.Place([page], ImportContributors.Check(new DraftContribution(additions), 1));

    [Fact]
    public void Start_AfterIntroduction_AndEnd_KeepTheirOrder()
    {
        var page = Page("paragraph", "heading", "paragraph");
        Place(page, new DraftAddition(0, DraftPlace.End, [Node("e1")]), new DraftAddition(0, DraftPlace.AfterIntroduction, [Node("i1"), Node("i2")]),
            new DraftAddition(0, DraftPlace.Start, [Node("s1")]), new DraftAddition(0, DraftPlace.End, [Node("e2")]));
        Assert.Equal(["s1", "paragraph", "i1", "i2", "heading", "paragraph", "e1", "e2"], Types(page));
    }

    [Fact]
    public void AfterIntroduction_OnAPageThatStartsWithAHeading_IsAfterTheFirstSection_AndAtTheEndWithoutOne()
    {
        var sections = Page("heading", "paragraph", "heading", "paragraph");
        Place(sections, new DraftAddition(0, DraftPlace.AfterIntroduction, [Node("chart")]));
        Assert.Equal(["heading", "paragraph", "chart", "heading", "paragraph"], Types(sections));

        var plain = Page("paragraph", "paragraph");
        Place(plain, new DraftAddition(0, DraftPlace.AfterIntroduction, [Node("chart")]));
        Assert.Equal(["paragraph", "paragraph", "chart"], Types(plain));

        var empty = Page();
        Place(empty, new DraftAddition(0, DraftPlace.AfterIntroduction, [Node("chart")]));
        Assert.Equal(["chart"], Types(empty));
    }

    [Fact]
    public void AnAnswer_NamingAPageTheDraftLacks_OrOverTheLimitsTogether_IsRefusedWhole()
    {
        Assert.Throws<InvalidDataException>(() => ImportContributors.Check(new DraftContribution([new DraftAddition(1, DraftPlace.End, [Node("paragraph")])]), 1));
        Assert.Throws<InvalidDataException>(() => ImportContributors.Check(new DraftContribution([new DraftAddition(-1, DraftPlace.End, [Node("paragraph")])]), 1));
        Assert.Throws<InvalidDataException>(() => ImportContributors.Check(new DraftContribution([new DraftAddition(0, (DraftPlace)9, [Node("paragraph")])]), 1));
        Assert.Throws<InvalidDataException>(() => ImportContributors.Check(new DraftContribution([new DraftAddition(0, DraftPlace.End, [Node("text")])]), 1));
        // 50 nodes are counted over all the additions: 30 and 21 is too many.
        var thirty = Enumerable.Repeat(Node("paragraph"), 30).ToList();
        var twentyOne = Enumerable.Repeat(Node("paragraph"), 21).ToList();
        Assert.Throws<InvalidDataException>(() => ImportContributors.Check(new DraftContribution([new DraftAddition(0, DraftPlace.Start, thirty), new DraftAddition(0, DraftPlace.End, twentyOne)]), 1));
        Assert.Equal(2, ImportContributors.Check(new DraftContribution([new DraftAddition(0, DraftPlace.Start, thirty), new DraftAddition(0, DraftPlace.End, thirty[..20])]), 1).Count);
    }

    [Fact]
    public void TheSizeLimit_CountsUtf8Bytes_SummedOverTheNodes()
    {
        // Each node: {"type":"paragraph","attrs":{"t":"ééé…"}} with 2-byte characters; two of them just over 512 KB in UTF-8, though under it in characters.
        var text = new string('é', ImportContributors.MaxBytes / 4);
        var node = JsonSerializer.SerializeToElement(new { type = "paragraph", attrs = new { t = text } }, Raw);
        Assert.Single(ImportContributors.Check(new ImportContribution([node])));
        Assert.Throws<InvalidDataException>(() => ImportContributors.Check(new ImportContribution([node, node])));
    }
}
