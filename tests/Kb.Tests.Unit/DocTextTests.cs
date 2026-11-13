using System.Text.Json;
using Kb.Core.Content;
using Kb.Plugins;

namespace Kb.Tests.Unit;

/// <summary>What the server reads from an editor node it does not know, and from one a plugin renders.</summary>
public sealed class DocTextTests
{
    // A paragraph, a plugin's block with two paragraphs inside, a paragraph with a plugin's inline node and a plugin's mark, a plugin's block inside a list.
    private const string Doc = """
        {"type":"doc","content":[
          {"type":"paragraph","content":[{"type":"text","text":"Before"}]},
          {"type":"probeBox","attrs":{"label":"Warning"},"content":[
            {"type":"paragraph","content":[{"type":"text","text":"inside the box"}]},
            {"type":"paragraph","content":[{"type":"text","text":"second line"}]}]},
          {"type":"paragraph","content":[
            {"type":"text","text":"Ask "},
            {"type":"probeMention","attrs":{"name":"gail"}},
            {"type":"text","text":" about "},
            {"type":"text","text":"this","marks":[{"type":"probeHighlight","attrs":{"color":"red"}}]}]},
          {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"probeBox","content":[{"type":"paragraph","content":[{"type":"text","text":"in a list"}]}]}]}]}
        ]}
        """;

    private static DocRenderers With(NodeRenderer renderer, List<string>? failed = null) =>
        new([new Registered<NodeRenderer>("kb.test.probe", renderer)], (plugin, e) => failed?.Add($"{plugin}: {e.Message}"));

    private static string Text(DocBlock block) => string.Concat(Assert.IsType<DocParagraph>(block).Runs.Select(r => r.Text));

    [Fact]
    public void UnknownNode_PlaceholderNotLoss()
    {
        // Search finds the page by the words inside a node the server does not know.
        Assert.Equal("Before\ninside the box second line\nAsk  about this\nin a list", DocText.Plain(Doc));

        // History and agents see that something is there, and what it says.
        Assert.Equal(
            ["Before", "", "[probeBox] inside the box second line", "", "Ask [probeMention] about this", "", "- [probeBox] in a list"],
            DocText.MarkdownLines(Doc));

        // An export gives a paragraph the same way.
        var blocks = DocText.Blocks(Doc);
        Assert.Equal(["Before", "[probeBox] inside the box second line", "Ask [probeMention] about this", "[probeBox] in a list"], blocks.Select(Text));
        Assert.Equal("•", Assert.IsType<DocParagraph>(blocks[3]).Marker);
    }

    [Fact]
    public void KnownNodes_ReadAsBefore()
    {
        const string doc = """
            {"type":"doc","content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Steps"}]},
              {"type":"orderedList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Open","marks":[{"type":"bold"}]},{"type":"hardBreak"},{"type":"text","text":"Settings"}]}]}]},
              {"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"Quoted"}]}]},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Server"}]}]}]}]},
              {"type":"horizontalRule"}
            ]}
            """;
        Assert.Equal(["## Steps", "", "1. **Open** Settings", "", "> Quoted", "", "| Server |", "| --- |", "", "---"], DocText.MarkdownLines(doc));
        Assert.Equal("Steps\nOpen Settings\nQuoted\nServer", DocText.Plain(doc));
    }

    [Fact]
    public void Renderer_GivesTheNodesTextMarkdownAndBlocks()
    {
        var renderers = With(new NodeRenderer
        {
            Type = "probeBox",
            Plain = n => "boxed " + n.GetProperty("attrs").GetProperty("label").GetString(),
            MarkdownLines = n => ["> **" + n.GetProperty("attrs").GetProperty("label").GetString() + "**", "> rendered"],
            Blocks = n => [new DocCode("text", n.GetProperty("attrs").GetProperty("label").GetString()!)],
        });
        const string doc = """{"type":"doc","content":[{"type":"probeBox","attrs":{"label":"Warning"},"content":[{"type":"paragraph","content":[{"type":"text","text":"hidden"}]}]},{"type":"blockquote","content":[{"type":"probeBox","attrs":{"label":"Nested"}}]}]}""";

        Assert.Equal("boxed Warning\nboxed Nested", DocText.Plain(doc, renderers));
        Assert.Equal(["> **Warning**", "> rendered", "", "> > **Nested**", "> > rendered"], DocText.MarkdownLines(doc, renderers));
        Assert.Equal([new DocCode("text", "Warning"), new DocCode("text", "Nested")], DocText.Blocks(doc, renderers));
    }

    [Fact]
    public void Renderer_WithOnePart_LeavesTheOthersToThePlaceholder()
    {
        var renderers = With(new NodeRenderer { Type = "probeMention", Plain = n => "@" + n.GetProperty("attrs").GetProperty("name").GetString() });

        Assert.Contains("Ask @gail about this", DocText.Plain(Doc, renderers));
        Assert.Contains("Ask [probeMention] about this", DocText.MarkdownLines(Doc, renderers));
        Assert.Contains("Ask @gail about this", DocText.Blocks(Doc, renderers).OfType<DocParagraph>().Select(Text)); // an inline node in an export is its plain text
    }

    [Fact]
    public void Renderer_ThatThrowsOrAnswersNothing_IsReported_AndThePlaceholderIsUsed()
    {
        var failed = new List<string>();
        var renderers = With(new NodeRenderer
        {
            Type = "probeBox",
            Plain = _ => throw new InvalidOperationException("No label."),
            MarkdownLines = _ => null!,
            Blocks = n => [new DocCode("text", n.GetProperty("missing").GetString()!)],
        }, failed);
        const string doc = """{"type":"doc","content":[{"type":"probeBox","content":[{"type":"paragraph","content":[{"type":"text","text":"kept"}]}]}]}""";

        Assert.Equal("kept", DocText.Plain(doc, renderers));
        Assert.Equal(["[probeBox] kept"], DocText.MarkdownLines(doc, renderers));
        Assert.Equal(["[probeBox] kept"], DocText.Blocks(doc, renderers).Select(Text));
        Assert.Equal(2, failed.Count); // the two that threw; answering nothing is not a failure
        Assert.All(failed, f => Assert.StartsWith("kb.test.probe: ", f, StringComparison.Ordinal));
    }

    [Fact]
    public void Renderer_ThatFailsWhileItsLinesAreRead_GivesThePlaceholder()
    {
        static IEnumerable<string> Broken()
        {
            yield return "first line";
            throw new InvalidOperationException("Failed on the second line.");
        }
        var failed = new List<string>();
        var renderers = With(new NodeRenderer { Type = "probeBox", MarkdownLines = _ => Broken() }, failed);
        const string doc = """{"type":"doc","content":[{"type":"probeBox","content":[{"type":"paragraph","content":[{"type":"text","text":"kept"}]}]}]}""";

        Assert.Equal(["[probeBox] kept"], DocText.MarkdownLines(doc, renderers)); // not half of the plugin's answer
        Assert.Equal(["kb.test.probe: Failed on the second line."], failed);
    }

    [Theory]
    [InlineData("null entry")]
    [InlineData("null runs")]
    [InlineData("null run")]
    [InlineData("null text")]
    [InlineData("null code")]
    [InlineData("null rows")]
    [InlineData("null row")]
    [InlineData("null cell")]
    [InlineData("null line")]
    public void Renderer_AnswerWithAHole_GivesThePlaceholder(string hole)
    {
        // The writers of Word, PDF and CSV files take a block as it is. A plugin's block with a null in it is refused here, where it can be blamed.
        IReadOnlyList<DocBlock> blocks = hole switch
        {
            "null entry" => [null!],
            "null runs" => [new DocParagraph(null!)],
            "null run" => [new DocParagraph([null!])],
            "null text" => [new DocParagraph([new DocRun(null!)])],
            "null code" => [new DocCode("text", null!)],
            "null rows" => [new DocTable(null!, false)],
            "null row" => [new DocTable([null!], false)],
            "null cell" => [new DocTable([[null!]], false)],
            _ => [new DocParagraph([new DocRun("fine")])],
        };
        var failed = new List<string>();
        var renderers = With(new NodeRenderer { Type = "probeBox", Blocks = _ => blocks, MarkdownLines = _ => hole == "null line" ? ["fine", null!] : ["fine"] }, failed);
        const string doc = """{"type":"doc","content":[{"type":"probeBox","content":[{"type":"paragraph","content":[{"type":"text","text":"kept"}]}]}]}""";

        if (hole == "null line") Assert.Equal(["[probeBox] kept"], DocText.MarkdownLines(doc, renderers));
        else Assert.Equal(["[probeBox] kept"], DocText.Blocks(doc, renderers).Select(Text));
        Assert.Single(failed);
    }

    [Fact]
    public void Renderer_ThatThrowsOnEveryNode_IsReportedOnce()
    {
        // What a page holds is its writer's to choose. A page full of nodes a renderer cannot read must not flood the log.
        var failed = new List<string>();
        var renderers = With(new NodeRenderer { Type = "probeBox", Plain = _ => throw new InvalidOperationException("No label.") }, failed);
        var doc = """{"type":"doc","content":[""" + string.Join(',', Enumerable.Repeat("""{"type":"probeBox","content":[{"type":"paragraph","content":[{"type":"text","text":"kept"}]}]}""", 50)) + "]}";

        Assert.Equal(string.Join('\n', Enumerable.Repeat("kept", 50)), DocText.Plain(doc, renderers));
        Assert.Equal(string.Join('\n', Enumerable.Repeat("kept", 50)), DocText.Plain(doc, renderers));
        Assert.Single(failed);
    }

    [Fact]
    public void DeeplyNestedDocument_IsReadWithoutTakingTheProcessDown()
    {
        // 5,000 levels of blockquote. The JSON reader refuses anything deeper than 64 levels, so such a document is not read at all
        // (and cannot be saved: the same reader checks a save). This pins that the walkers, which are recursive, never meet one.
        var doc = """{"type":"doc","content":[""" + string.Concat(Enumerable.Repeat("""{"type":"blockquote","content":[""", 5000))
            + """{"type":"paragraph","content":[{"type":"text","text":"deep"}]}""" + string.Concat(Enumerable.Repeat("]}", 5000)) + "]}";

        Assert.Equal("", DocText.Plain(doc));
        Assert.Empty(DocText.MarkdownLines(doc));
        Assert.Empty(DocText.Blocks(doc));
    }

    [Fact]
    public void CoreNodes_AreTheEditorsOwn()
    {
        // A plugin cannot take over one of these; see PluginBootstrap.
        string[] editor = ["doc", "paragraph", "text", "heading", "codeBlock", "blockquote", "bulletList", "orderedList", "listItem", "taskList", "taskItem",
            "table", "tableRow", "tableHeader", "tableCell", "horizontalRule", "hardBreak", "image"];
        Assert.Equal(editor.Order(), DocText.CoreNodes.Order());
        using var none = JsonDocument.Parse("{}");
        Assert.Null(DocRenderers.None.Plain(none.RootElement));
    }
}
