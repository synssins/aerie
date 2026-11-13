using System.Text.Json;

namespace Kb.Plugins;

/// <summary>A run of text with its formatting.</summary>
public sealed record DocRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, bool Code = false, string? Href = null);

/// <summary>A page flattened to simple blocks, for exporters that lay pages out themselves (Word, PDF).</summary>
public abstract record DocBlock;
/// <summary>Heading is 0 for body text. Marker is the list bullet or number, if any. Indent counts list and quote nesting.</summary>
public sealed record DocParagraph(IReadOnlyList<DocRun> Runs, int Heading = 0, string? Marker = null, int Indent = 0, bool Quote = false) : DocBlock;
public sealed record DocCode(string Language, string Text) : DocBlock;
public sealed record DocTable(IReadOnlyList<IReadOnlyList<string>> Rows, bool HeaderRow) : DocBlock;
public sealed record DocImage(Guid? ImageId, string Alt, int? Width) : DocBlock;
public sealed record DocRule : DocBlock;

/// <summary>
/// How the server reads an editor node type a plugin adds. The node is the stored JSON of one node (<c>type</c>, <c>attrs</c>, <c>content</c>).
/// Each part is optional. Without one, the server uses a placeholder: the text inside the node, with <c>[type]</c> before it in Markdown and exports.
/// A renderer must not throw and must be quick: it runs on every save (search), every diff and every export of a page that holds the node.
/// </summary>
public sealed class NodeRenderer
{
    /// <summary>The node's <c>type</c> in the editor document. Not a type the editor has by itself, and not one another plugin renders.</summary>
    public required string Type { get; init; }
    /// <summary>The words search finds the page by.</summary>
    public Func<JsonElement, string>? Plain { get; init; }
    /// <summary>The node as Markdown lines: what history compares, and what an agent reads.</summary>
    public Func<JsonElement, IEnumerable<string>>? MarkdownLines { get; init; }
    /// <summary>The node as blocks of a Word or PDF export.</summary>
    public Func<JsonElement, IReadOnlyList<DocBlock>>? Blocks { get; init; }
}

public static class ContentExtensions
{
    /// <summary>Registers how the server reads an editor node type of this plugin.</summary>
    public static IPluginApp EditorNode(this IPluginApp app, NodeRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(renderer);
        return app.Add(renderer);
    }
}
