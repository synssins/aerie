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
    // A page is stored as its writer sent it, so read with care: a renderer must not throw on a node of an odd shape.
    private static string Text(JsonElement node) =>
        node.TryGetProperty("content", out var kids) && kids.ValueKind == JsonValueKind.Array
            ? string.Concat(kids.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.Object && k.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : ""))
            : "";
    // #endregion
}
