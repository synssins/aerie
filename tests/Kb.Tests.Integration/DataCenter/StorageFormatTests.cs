using Kb.Web.DataCenter;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>The round trip of contracts/behaviour/representations.md: B to B' is equivalent, B' is a fixed point, opaque nodes are untouched.</summary>
public sealed class StorageFormatTests
{
    private static string Trip(string b) => StorageFormat.FromDoc(StorageFormat.ToDoc(b).ToJsonString());

    /// <summary>Bodies aerie maps to editor nodes, written back exactly.</summary>
    [Theory]
    [InlineData("<p>Hello <strong>team</strong></p>")]
    [InlineData("<h1>One</h1><h3>Three</h3><p>a<br />b</p>")]
    [InlineData("<p><a href=\"https://example.org\">link <strong>bold <em>both</em></strong></a> and <code>x &lt; y</code></p>")]
    [InlineData("<ul><li>one</li><li>two<ul><li>deep</li></ul></li></ul><ol><li>first</li></ol>")]
    [InlineData("<blockquote><p>quoted</p></blockquote><hr /><pre>raw  text</pre>")]
    [InlineData("<table><tbody><tr><th><p>H</p></th><td colspan=\"2\"><p>c</p></td></tr></tbody></table>")]
    [InlineData("<ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>done</ac:task-body></ac:task></ac:task-list>")]
    [InlineData("<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">sql</ac:parameter><ac:plain-text-body><![CDATA[select 1 < 2;]]></ac:plain-text-body></ac:structured-macro>")]
    [InlineData("<ac:image ac:alt=\"diagram\"><ri:url ri:value=\"/api/v1/images/x\" /></ac:image>")]
    [InlineData("")]
    public void Mapped_BodiesComeBackAsWritten(string b)
    {
        Assert.Equal(b, Trip(b));
        Assert.DoesNotContain(StorageFormat.OpaqueBlock, StorageFormat.ToDoc(b).ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>Anything aerie does not map is an opaque node, byte for byte, in its place.</summary>
    [Theory]
    [InlineData("<p>before</p><ac:structured-macro ac:name=\"info\" ac:macro-id=\"m1\"><ac:rich-text-body><p>Note  it</p></ac:rich-text-body></ac:structured-macro><p>after</p>")]
    [InlineData("<p>Hi <ac:link><ri:page ri:content-title=\"Home\" ri:space-key=\"OPS\" /></ac:link> and <span style=\"color: red;\">red</span>&nbsp;there</p>")]
    [InlineData("<p style=\"text-align: center;\">mid</p>")]
    [InlineData("<ul><li><p>wrapped</p></li></ul>")]
    [InlineData("<custom:thing xmlns:custom=\"urn:x\" />")]
    [InlineData("<p><b>old bold</b> <em><strong>reversed</strong></em></p>")]
    [InlineData("<ac:layout><ac:layout-section ac:type=\"two_equal\"><ac:layout-cell><p>a</p></ac:layout-cell><ac:layout-cell><p>b</p></ac:layout-cell></ac:layout-section></ac:layout>")]
    public void Opaque_NodesAreKeptExactly(string b)
    {
        var once = Trip(b);
        Assert.Equal(once, Trip(once)); // a fixed point
        foreach (var macro in new[] { "<ac:structured-macro ac:name=\"info\" ac:macro-id=\"m1\"><ac:rich-text-body><p>Note  it</p></ac:rich-text-body></ac:structured-macro>",
                     "<ac:link><ri:page ri:content-title=\"Home\" ri:space-key=\"OPS\" /></ac:link>", "<span style=\"color: red;\">red</span>", "<b>old bold</b>" })
            if (b.Contains(macro, StringComparison.Ordinal)) Assert.Contains(macro, once, StringComparison.Ordinal);
    }

    [Fact]
    public void Entities_AreReadAsTheirCharacters()
    {
        Assert.Equal("<p>a b — &amp; &lt;</p>", Trip("<p>a&nbsp;b &mdash; &amp; &lt;</p>"));
        Assert.Equal("<pre>x&gt;</pre>", Trip("<pre>x&#62;</pre>"));
    }

    [Fact]
    public void WhitespaceBetweenBlocks_IsNotContent() => Assert.Equal("<p>a</p><p>b</p>", Trip("<p>a</p>\n  <p>b</p>\n"));

    /// <summary>What aerie's editor makes is written as storage, and reads back as the same document.</summary>
    [Fact]
    public void EditorDocuments_RoundTrip()
    {
        const string doc = """{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Steps"}]},{"type":"paragraph","content":[{"type":"text","marks":[{"type":"bold"}],"text":"Do"},{"type":"text","text":" it"}]},{"type":"codeBlock","attrs":{"language":"powershell"},"content":[{"type":"text","text":"Get-Item ]]> x"}]},{"type":"probeBox","attrs":{"n":1}}]}""";
        var storage = StorageFormat.FromDoc(doc);
        Assert.Contains("<h2>Steps</h2>", storage, StringComparison.Ordinal);
        Assert.Contains("ac:name=\"aerie-node\"", storage, StringComparison.Ordinal);
        Assert.Equal(storage, Trip(storage));
        Assert.Equal("powershell", StorageFormat.ToDoc(storage)["content"]![2]!["attrs"]!["language"]!.GetValue<string>());
        Assert.Equal("probeBox", StorageFormat.ToDoc(storage)["content"]![3]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("<p>open")]
    [InlineData("<p>a</b>")]
    [InlineData("<p a=b>x</p>")]
    [InlineData("<p>&bogus;</p>")]
    [InlineData("<p>a & b</p>")]
    [InlineData("<x:y />")]
    [InlineData("<!DOCTYPE p><p />")]
    [InlineData("<ac:layout><ac:layout-section ac:type=\"three_equal\"><ac:layout-cell /></ac:layout-section></ac:layout>")]
    [InlineData("<p>x</p><ac:layout><ac:layout-section ac:type=\"single\"><ac:layout-cell /></ac:layout-section></ac:layout>")]
    public void Broken_BodiesAreRefused(string b) => Assert.Throws<StorageException>(() => StorageFormat.ToDoc(b));

    [Fact]
    public void Macros_AreDescribed()
    {
        var nodes = StorageFormat.Parse("<ac:structured-macro ac:name=\"panel\" ac:macro-id=\"p1\"><ac:parameter ac:name=\"title\">T</ac:parameter><ac:rich-text-body><p>in</p></ac:rich-text-body></ac:structured-macro>");
        var (name, body, parameters) = StorageFormat.Describe(StorageFormat.Macros(nodes).Single());
        Assert.Equal(("panel", "<p>in</p>", "T"), (name, body, parameters["title"]));
    }
}

/// <summary>Every message object the surface makes (common.md, Message objects; Q-0019).</summary>
public sealed class DcMessageTests
{
    [Fact]
    public void AMessage_HasEmptyArgs_AndItsValuesInTheText()
    {
        var m = Kb.Web.DataCenter.DcRest.Message("aerie.longtask.space.delete", "Deleting space DS16");
        Assert.Equal("aerie.longtask.space.delete", m["key"]!.GetValue<string>());
        Assert.Empty(m["args"]!.AsArray());
        Assert.Equal("Deleting space DS16", m["translation"]!.GetValue<string>());
    }

    /// <summary>No code builds a message object by hand: an args list with items cannot come back.</summary>
    [Fact]
    public void NoOtherCode_WritesArgs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        var hits = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "Kb.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select(l => (File: Path.GetFileName(f), Line: l)))
            .Where(x => x.Line.Contains("[\"args\"]", StringComparison.Ordinal) && x.File != "DcRest.cs").ToList();
        Assert.Empty(hits);
    }
}
