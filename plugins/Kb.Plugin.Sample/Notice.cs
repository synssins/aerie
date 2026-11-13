using System.Text.Json;
using Kb.Plugins;

namespace Kb.Plugin.Sample;

/// <summary>The sample's editor block: a line of a page shown as a notice. This is the server's half; the browser's half is in ui/index.js.</summary>
public static class Notice
{
    // #region guide:editornode
    /// <summary>
    /// How the server reads the block for history, agents and exports.
    /// It gives no Plain: search then finds the page by the text inside the block, which is what a notice needs.
    /// </summary>
    public static NodeRenderer Renderer { get; } = new()
    {
        Type = "sampleNotice",
        MarkdownLines = node => ["> **Notice:** " + Text(node)],
        Blocks = node => [new DocParagraph([new DocRun("Notice: ", Bold: true), new DocRun(Text(node))], Quote: true)],
    };

    // The node is the stored JSON of the block: { "type": "sampleNotice", "content": [ { "type": "text", "text": "..." } ] }.
    private static string Text(JsonElement node) =>
        node.TryGetProperty("content", out var kids) ? string.Concat(kids.EnumerateArray().Select(k => k.TryGetProperty("text", out var t) ? t.GetString() : "")) : "";
    // #endregion
}
