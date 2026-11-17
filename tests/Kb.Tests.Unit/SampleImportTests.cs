using System.Text;
using System.Text.Json;
using Kb.Plugin.Sample;
using Kb.Plugins;

namespace Kb.Tests.Unit;

/// <summary>The sample plugin's import and draft contributors, called as the core calls them.</summary>
public sealed class SampleImportTests
{
    private static ImportedFile File(string name, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return new ImportedFile(Guid.NewGuid(), "a/" + name, name, data.Length, _ => Task.FromResult<Stream>(new MemoryStream(data, false)));
    }

    [Fact]
    public async Task ANoticeFile_BecomesANoticeNode_AndOtherFilesAddNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var added = await new NoticeFromFile().ContributeAsync(File("lab.notice.txt", "  Wear gloves.  \nmore"), ct);
        var node = Assert.Single(added!.Nodes);
        Assert.Equal("sampleNotice", node.GetProperty("type").GetString());
        Assert.Equal("Wear gloves.", node.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Null(await new NoticeFromFile().ContributeAsync(File("lab.txt", "Wear gloves."), ct));
        Assert.Null(await new NoticeFromFile().ContributeAsync(File("empty.notice.txt", ""), ct));
    }

    private static DraftPage Page(params object[] blocks) => new("Lab", JsonSerializer.Serialize(new { type = "doc", content = blocks }));

    private static object Para(string text) => new { type = "paragraph", content = new[] { new { type = "text", text } } };

    [Fact]
    public async Task ANoticeParagraph_InTheDraft_IsAddedAfterTheIntroduction_OnceAPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var draft = new ImportDraft(Guid.NewGuid(), [Page(Para("Intro.")), Page(Para("Steps."), Para("Notice: Wear gloves.")), Page(new { type = "sampleNotice" }, Para("Notice: twice"))], ["lab.txt"]);
        var added = await new NoticeFromDraft().ContributeAsync(draft, ct);
        var one = Assert.Single(added!.Additions);
        Assert.Equal((1, DraftPlace.AfterIntroduction), (one.Page, one.Place));
        Assert.Equal("Wear gloves.", Assert.Single(one.Nodes).GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Null(await new NoticeFromDraft().ContributeAsync(new ImportDraft(Guid.NewGuid(), [Page(Para("Notice:"))], []), ct));
    }
}
