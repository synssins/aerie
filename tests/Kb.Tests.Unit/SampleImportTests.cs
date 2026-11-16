using System.Text;
using Kb.Plugin.Sample;
using Kb.Plugins;

namespace Kb.Tests.Unit;

/// <summary>The sample plugin's import contributor, called as the core calls it.</summary>
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
}
