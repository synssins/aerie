using System.Text.Json.Nodes;
using Kb.Core.Content;
using Kb.Web.Import;

namespace Kb.Tests.Integration;

/// <summary>Format by construction, on the shape of a real page: a procedure written out twice, a title stuck to a list item.</summary>
public sealed class DocFormatTests
{
    private static JsonObject T(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonObject Br() => new() { ["type"] = "hardBreak" };
    private static JsonObject P(params JsonNode[] content) => new() { ["type"] = "paragraph", ["content"] = new JsonArray(content) };
    private static JsonObject H(string text, int level = 2) => new() { ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = level }, ["content"] = new JsonArray(T(text)) };
    private static JsonObject Li(params JsonNode[] content) => new() { ["type"] = "listItem", ["content"] = new JsonArray(P(content)) };
    private static JsonObject Ol(params JsonObject[] items) => new() { ["type"] = "orderedList", ["attrs"] = new JsonObject { ["start"] = 1 }, ["content"] = new JsonArray(items) };

    private const string Approach = "Approach the Flibbertigibbet Autoscaler calmly and avoid eye contact.";
    private const string Say = "Say the maintenance phrase out loud: \"I acknowledge your service.\"";
    private const string Run = "Run the reset from the jump host:";
    private const string Wait = "Wait 90 seconds. Do not hum.";
    private const string Confirm = "Confirm the counters below are back to normal.";

    /// <summary>The page as an import left it: steps under a title, "Procedure" glued to the last step, then the same steps again.</summary>
    private static JsonObject Page() => new()
    {
        ["type"] = "doc",
        ["content"] = new JsonArray(
            P(T("Applies to: apim-prod.")),
            H(Approach),
            Ol(Li(T(Say)), Li(T(Run)), Li(T(Wait)), Li(T(Confirm), Br(), Br(), T("Procedure"))),
            Ol(Li(T(Approach)), Li(T(Say)), Li(T(Run)), Li(T(Wait)), Li(T(Confirm))),
            new JsonObject { ["type"] = "codeBlock", ["attrs"] = new JsonObject { ["language"] = "bash" }, ["content"] = new JsonArray(T("sudo whimsy-ctl reset --gently")) },
            H("Escalation"),
            P(T("If it resets but then looks at you, page the on-call engineer."))),
    };

    private static string Plain(JsonObject doc) => DocText.Plain(doc.ToJsonString());

    [Fact]
    public void TheUnits_AreEachParagraphHeadingListItemAndLine_SoATitleStuckToAStepIsItsOwn()
    {
        var units = DocFormat.Units(Page());
        Assert.Equal(["text", "text", "item", "item", "item", "item", "line", "item", "item", "item", "item", "item", "block", "text", "text"], units.Select(u => u.Kind));
        Assert.Equal("Procedure", units[6].Text);
        Assert.Equal(5, units[6].ItemOf);
        Assert.Contains("[6] (line of the list item above) Procedure", DocFormat.Lines(units), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedProcedure_IsRemovedAndListed_TheTitleBecomesAHeading_AndNothingElseIsLost()
    {
        // As the model answers: the first copy (title and steps) is the repeat; "Procedure" is the title.
        var roles = new Dictionary<int, string> { [1] = "duplicate", [2] = "duplicate", [3] = "duplicate", [4] = "duplicate", [5] = "duplicate", [6] = "heading2" };
        var (doc, removed) = DocFormat.Restructure(Page(), roles);

        var removal = Assert.Single(removed);
        Assert.Equal([1, 2, 3, 4, 5], removal.Units);
        Assert.StartsWith("Approach the Flibbertigibbet", removal.Text, StringComparison.Ordinal);
        Assert.StartsWith("Approach the Flibbertigibbet", removal.SameAs, StringComparison.Ordinal); // the step that says the same

        var blocks = doc["content"]!.AsArray().Select(n => n!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["paragraph", "heading", "orderedList", "codeBlock", "heading", "paragraph"], blocks);
        Assert.Equal("Procedure", DocText.Plain(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(doc["content"]![1]!.DeepClone()) }.ToJsonString()).Trim());
        Assert.Equal(5, doc["content"]![2]!["content"]!.AsArray().Count);
        Assert.Equal("bash", doc["content"]![3]!["attrs"]!["language"]!.GetValue<string>());

        // Every word stays except the repeat's.
        Assert.True(DocFormat.OnlyPageWords(Plain(Page()), Plain(doc)));
        Assert.Equal(2, Plain(Page()).Split("I acknowledge your service").Length - 1); // two copies before
        Assert.Equal(1, Plain(doc).Split("I acknowledge your service").Length - 1);    // one after
        foreach (var kept in new[] { "Applies to: apim-prod.", Approach, Run, Wait, Confirm, "whimsy-ctl reset --gently", "Escalation", "page the on-call engineer" })
            Assert.Contains(kept, Plain(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelKeepsTheFirstCopy_AndATitleStuckToAStep_IsAHeadingOfItsOwn()
    {
        // As the real model answered for this page: the second list is the repeat; it said nothing of "Procedure".
        var (doc, removed) = DocFormat.Restructure(Page(), new Dictionary<int, string> { [7] = "duplicate", [8] = "duplicate", [9] = "duplicate", [10] = "duplicate", [11] = "duplicate" });
        Assert.Equal([7, 8, 9, 10, 11], Assert.Single(removed).Units);
        var blocks = doc["content"]!.AsArray().Select(n => n!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["paragraph", "heading", "orderedList", "heading", "codeBlock", "heading", "paragraph"], blocks);
        var lastStep = DocText.Plain(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(doc["content"]![2]!.DeepClone()) }.ToJsonString());
        Assert.DoesNotContain("Procedure", lastStep, StringComparison.Ordinal); // no longer glued to step 4
        Assert.True(DocFormat.OnlyPageWords(Plain(Page()), Plain(doc)));

        // The model calls "Procedure" a duplicate too: nothing else says it, so it stays, and as a heading, not glued back.
        var again = DocFormat.Restructure(Page(), new Dictionary<int, string> { [6] = "duplicate", [7] = "duplicate", [8] = "duplicate", [9] = "duplicate", [10] = "duplicate", [11] = "duplicate" });
        Assert.Equal(blocks, again.Doc["content"]!.AsArray().Select(n => n!["type"]!.GetValue<string>()));
        Assert.Equal("Procedure", again.Doc["content"]![3]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ADuplicateWithNoCopyThatStays_IsKept_AndThePersonCanKeepOneThatWasRemoved()
    {
        // The model calls the escalation paragraph a duplicate: nothing else says it, so it stays.
        var (doc, removed) = DocFormat.Restructure(Page(), new Dictionary<int, string> { [14] = "duplicate" });
        Assert.Empty(removed);
        Assert.Contains("on-call engineer", Plain(doc), StringComparison.Ordinal);

        // Both copies called duplicates: one of them stays.
        var both = DocFormat.Restructure(Page(), new Dictionary<int, string> { [2] = "duplicate", [8] = "duplicate" });
        Assert.Equal([2], Assert.Single(both.Removed).Units);
        Assert.Contains(Say, Plain(both.Doc), StringComparison.Ordinal);

        // Kept by the person: built again with the same roles, the step is back.
        var kept = DocFormat.Restructure(Page(), new Dictionary<int, string> { [2] = "duplicate" }, new HashSet<int> { 2 });
        Assert.Empty(kept.Removed);
        Assert.Equal(Plain(Page()).Split(Say).Length, Plain(kept.Doc).Split(Say).Length);
    }

    [Fact]
    public void Copies_AreTheSameAfterCaseSpacingNumbersAndEndPunctuation_NotWhenTheyDiffer()
    {
        Assert.True(DocFormat.Same("1. Wait 90 seconds.", "wait   90 seconds"));
        Assert.False(DocFormat.Same("Wait 90 seconds.", "Wait 60 seconds."));
        Assert.True(DocFormat.Same("Run the reset from the jump host and confirm the counters are back to normal", "Run the reset from the jump host and confirm all counters are back to normal"));
        Assert.False(DocFormat.Same("", ""));
    }

    private static JsonObject T(string text, string mark) => new() { ["type"] = "text", ["text"] = text, ["marks"] = new JsonArray(new JsonObject { ["type"] = mark }) };

    /// <summary>Label and value items run together in one paragraph, as the demo pages have them.</summary>
    private static JsonObject Metadata() => new()
    {
        ["type"] = "doc",
        ["content"] = new JsonArray(
            P(T("Applies to:", "bold"), T(" "), T("kv-platform-prod", "code"), T(" and anything within ten feet of it. "), T("Owner:", "bold"),
              T(" Cloud Platform Engineering. "), T("Last verified:", "bold"), T(" after the incident we don't talk about.")),
            H("Symptoms")),
    };

    [Fact]
    public void ARunTogetherParagraph_IsCutWhereTheModelSaysEachItemStarts_KeepingItsMarksAndEveryWord()
    {
        var splits = new Dictionary<int, IReadOnlyList<string>> { [0] = ["Owner:", "Last verified:"] };
        var (doc, removed) = DocFormat.Restructure(Metadata(), new Dictionary<int, string> { [0] = "paragraph" }, null, splits);
        Assert.Empty(removed);
        var blocks = doc["content"]!.AsArray();
        Assert.Equal(["paragraph", "paragraph", "paragraph", "heading"], blocks.Select(n => n!["type"]!.GetValue<string>()));
        string Line(int i) => DocText.Plain(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks[i]!.DeepClone()) }.ToJsonString()).Trim();
        Assert.Equal("Applies to: kv-platform-prod and anything within ten feet of it.", Line(0));
        Assert.Equal("Owner: Cloud Platform Engineering.", Line(1));
        Assert.Equal("Last verified: after the incident we don't talk about.", Line(2));
        // Each label keeps its bold, the value its code mark.
        Assert.Equal("bold", blocks[1]!["content"]![0]!["marks"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Owner:", blocks[1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("code", blocks[0]!["content"]![2]!["marks"]![0]!["type"]!.GetValue<string>());
        Assert.True(DocFormat.OnlyPageWords(Plain(Metadata()), Plain(doc)));
    }

    [Fact]
    public void ACutThatIsNotInTheText_OrNotAtAWordStart_OrInAHeading_IsIgnored()
    {
        var splits = new Dictionary<int, IReadOnlyList<string>> { [0] = ["Nowhere:", "wner:", "Applies to:", "Owner: Cloud Platform Engineering. Last verified: after the incident we don't talk about. And more"], [1] = ["toms"] };
        var (doc, _) = DocFormat.Restructure(Metadata(), new Dictionary<int, string> { [0] = "paragraph" }, null, splits);
        Assert.Equal(["paragraph", "heading"], doc["content"]!.AsArray().Select(n => n!["type"]!.GetValue<string>()));
        Assert.Equal(Plain(Metadata()), Plain(doc));
    }

    private static readonly IReadOnlyDictionary<int, string> NoRoles = new Dictionary<int, string>();

    [Fact]
    public void ASpellingFix_IsMadeInText_ButNeverInCode_Identifiers_OrQuotedText()
    {
        var page = new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(
                P(T("Mofdnitoring shows the Kraken. Run "), T("Mofdnitoring", "code"), T(" on kv-platfrom-prod.")),
                new JsonObject { ["type"] = "codeBlock", ["attrs"] = new JsonObject { ["language"] = "bash" }, ["content"] = new JsonArray(T("echo Mofdnitoring")) },
                P(T("The log says \"Mofdnitoring failed\" and Mofdnitoring stops.")),
                Ol(Li(T("Check teh pods.")))),
        };
        var fix = new DocFormat.Fix("Mofdnitoring", "Monitoring");
        var fixes = new Dictionary<int, IReadOnlyList<DocFormat.Fix>>
        {
            [0] = [fix, new("kv-platfrom-prod", "kv-platform-prod")], [1] = [fix], [2] = [fix], [3] = [new("teh", "the")],
        };
        var (doc, _) = DocFormat.Restructure(page, NoRoles, null, null, fixes);
        var blocks = doc["content"]!.AsArray();
        string Line(int i) => DocText.Plain(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks[i]!.DeepClone()) }.ToJsonString()).Trim();
        Assert.Equal("Monitoring shows the Kraken. Run Mofdnitoring on kv-platfrom-prod.", Line(0)); // inline code and a host name stay
        Assert.Equal("echo Mofdnitoring", Line(1));                                                  // a code block stays
        Assert.Equal("The log says \"Mofdnitoring failed\" and Monitoring stops.", Line(2));        // a quoted message stays
        Assert.Equal("Check the pods.", Line(3));                                                     // list items too
        Assert.Equal("code", blocks[0]!["content"]![1]!["marks"]![0]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Mofdnitoring", "Monitoring")]
    [InlineData("teh", "the")]
    [InlineData("recieve", "receive")]
    [InlineData("a", "an")]
    [InlineData("dont", "don't")]
    [InlineData("kubernetes", "Kubernetes")]
    public void ACorrection_IsAllowed(string from, string to) => Assert.Null(DocFormat.FixError(new(from, to)));

    [Theory]
    [InlineData("40", "50")]                       // a number
    [InlineData("enabled", "disabled")]            // the opposite
    [InlineData("Owner", "Manager")]               // another word
    [InlineData("restart", "reboot")]              // a rewording
    [InlineData("the pods", "all of the pods")]    // added words
    [InlineData("Check", "")]                      // a removal
    public void AChangeOfContent_IsNotACorrection(string from, string to)
    {
        Assert.NotNull(DocFormat.FixError(new(from, to)));
        // Nor is it made, whatever the caller passes.
        var page = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(P(T($"Check {from} now."))) };
        var (doc, _) = DocFormat.Restructure(page, NoRoles, null, null, new Dictionary<int, IReadOnlyList<DocFormat.Fix>> { [0] = [new(from, to)] });
        Assert.Equal(Plain(page), Plain(doc));
    }

    [Fact]
    public void ASplit_OnAPartWhoseRoleIsNotParagraph_IsIgnored()
    {
        var splits = new Dictionary<int, IReadOnlyList<string>> { [0] = ["Owner:", "Last verified:"] };
        foreach (var roles in new[] { NoRoles, new Dictionary<int, string> { [0] = "keep" }, new Dictionary<int, string> { [0] = "note" } })
        {
            var (doc, _) = DocFormat.Restructure(Metadata(), roles, null, splits);
            Assert.Single(doc["content"]!.AsArray(), n => n!["type"]!.GetValue<string>() != "heading");
        }
    }

    [Fact]
    public void AWordWithACapitalAfterItsFirstLetter_IsWrittenLikeAnIdentifier_AndStays()
    {
        var page = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(P(T("Check MOnitoring and AKs and Monitorng."))) };
        var fixes = new Dictionary<int, IReadOnlyList<DocFormat.Fix>> { [0] = [new("MOnitoring", "Monitoring"), new("AKs", "AKS"), new("Monitorng", "Monitoring")] };
        var (doc, _) = DocFormat.Restructure(page, NoRoles, null, null, fixes);
        Assert.Equal("Check MOnitoring and AKs and Monitoring.", Plain(doc).Trim());
    }
}
