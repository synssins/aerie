using System.Text.Json;

namespace Kb.Plugins;

/// <summary>
/// One file of an AI import, given to an <see cref="IImportContributor"/> after every virus scanner passed it.
/// <paramref name="Path"/> is where it was in the upload (folders, a zip's entries, a mail's attachments, joined with "/");
/// <paramref name="FileName"/> is its last part. <paramref name="OpenReadAsync"/> gives a new read-only stream at the first byte on each call.
/// </summary>
public sealed record ImportedFile(Guid ImportItemId, string Path, string FileName, long Length, Func<CancellationToken, Task<Stream>> OpenReadAsync);

/// <summary>
/// Editor nodes to add to the end of the draft made from a file: each the stored JSON of one block node, as a page holds it
/// (<c>{"type": "...", "attrs": {...}, "content": [...]}</c>). At most 50 nodes and 512 KB of JSON.
/// </summary>
public sealed record ImportContribution(IReadOnlyList<JsonElement> Nodes);

/// <summary>
/// Reads the files of an AI import and adds content of its own to the draft, before the importer reviews and accepts it.
/// It is called once per file of an item the AI drafted (not a blocked file, a zip itself, or a picture inside a document), in its own
/// scope, as its plugin (an actor of kind Plugin, source "import"). It is not called for an item that holds a secret or that goes to a
/// sensitive space. A call that throws, runs past 10 seconds, or answers more than the limits of <see cref="ImportContribution"/> adds
/// nothing and counts as a failure of the plugin; the import goes on.
/// </summary>
public interface IImportContributor
{
    /// <summary>Null, or nodes to add, for this file.</summary>
    Task<ImportContribution?> ContributeAsync(ImportedFile file, CancellationToken ct);
}

public sealed record ImportContributorReg(Type Contributor);

public static class ImportExtensions
{
    /// <summary>Registers a contributor to AI imports. It is made for each call, with the plugin's services.</summary>
    public static IPluginApp ImportContributor<TContributor>(this IPluginApp app) where TContributor : class, IImportContributor
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new ImportContributorReg(typeof(TContributor)));
    }

    /// <summary>Registers a contributor to AI imports' drafts. It is made for each call, with the plugin's services.</summary>
    public static IPluginApp DraftContributor<TContributor>(this IPluginApp app) where TContributor : class, IDraftContributor
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new DraftContributorReg(typeof(TContributor)));
    }

    /// <summary>
    /// Claims file kinds the app does not read, by extension (<c>".abc"</c>: a dot, then 1 to 16 letters or digits; any case). A file of
    /// such a kind in an import is not failed: it gets a draft with one empty page, the AI is not called for it, and the plugin's import
    /// contributors read it and fill the page. A kind the app reads itself is unchanged, whatever a plugin claims. Throws on an extension
    /// that is not of that form.
    /// </summary>
    public static IPluginApp ImportFileKinds(this IPluginApp app, params string[] extensions)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Length == 0) throw new ArgumentException("Name at least one extension.", nameof(extensions));
        foreach (var e in extensions)
            if (e is null || e.Length is < 2 or > 17 || e[0] != '.' || !e.Skip(1).All(char.IsAsciiLetterOrDigit))
                throw new ArgumentException($"\"{e}\" is not an extension: a dot, then 1 to 16 letters or digits.", nameof(extensions));
        return app.Add(new ImportFileKindReg([.. extensions.Select(e => e.ToLowerInvariant()).Distinct()]));
    }
}

/// <summary>File kinds a plugin reads for import, by extension, in lower case with the dot.</summary>
public sealed record ImportFileKindReg(IReadOnlyList<string> Extensions);

/// <summary>One page of an import's draft, read-only: its title, and its content as a page stores it (a <c>doc</c> node, as JSON).</summary>
public sealed record DraftPage(string Title, string ContentJson);

/// <summary>
/// The draft of one AI import item, as the importer is about to see it: the pages the AI drafted, with what the import contributors
/// added already in them. <paramref name="Files"/> are the paths of the item's files that were read (as <see cref="ImportedFile.Path"/>).
/// </summary>
public sealed record ImportDraft(Guid ImportItemId, IReadOnlyList<DraftPage> Pages, IReadOnlyList<string> Files)
{
    /// <summary>
    /// 0.10.0. The pictures of the item that were sent to the AI (a picture imported on its own, or one inside a document), in the order
    /// they were sent, with the diagram the AI read from each when it is one. Empty before 0.10.0, and for an item with no picture.
    /// </summary>
    public IReadOnlyList<DraftPicture> Pictures { get; init; } = [];

    /// <summary>
    /// 0.11.0. The blocks of <see cref="Pages"/> that import tools added (the tool conversation runs before the draft
    /// contributors): where each stands and which plugin's tool added it, so a contributor can tell what a tool already made. Empty when
    /// no tool added anything.
    /// </summary>
    public IReadOnlyList<DraftToolBlock> ToolBlocks { get; init; } = [];
}

/// <summary>
/// 0.11.0. One block an import tool added: <paramref name="Page"/> (0 is the first) and <paramref name="Block"/> (its index among
/// the page's top-level blocks, as <see cref="ImportDraft.Pages"/> gives them to this contributor: the app counts it anew for each, after
/// what the ones before it added), the plugin and the name of the tool.
/// </summary>
public sealed record DraftToolBlock(int Page, int Block, string PluginId, string Tool)
{
    /// <summary>
    /// The index, on the same page and counted the same way, of the block the tool's nodes were put after (the block that shows the
    /// content the call was about, such as the drawing); -1 when that block is no longer on the page.
    /// </summary>
    public int After { get; init; } = -1;
}

/// <summary>
/// 0.10.0. A picture of the draft. <paramref name="Src"/> is its image node's <c>attrs.src</c> in the draft (a node placed
/// <see cref="DraftPlace.AfterPicture"/> names it); <paramref name="Page"/> is the page (0 is the first) that shows it.
/// <see cref="Diagram"/> is null when the picture is not a diagram, or the AI could not read one from it.
/// </summary>
public sealed record DraftPicture(string Src, string FileName, int Page)
{
    public PictureDiagram? Diagram { get; init; }

    /// <summary>The picture's size in pixels, read from its file (0 when the app cannot tell): areas are fractions of these sides.</summary>
    public int Width { get; init; }

    public int Height { get; init; }
}

/// <summary>
/// 0.10.0. What the AI read from a picture of a diagram (a whiteboard, a sketch, a drawing). It is the AI's reading, not the truth: labels
/// may be misread. The app has bounded it (<see cref="DiagramLimits"/>): every id an item names resolves, nesting has no loops, and what
/// did not fit was dropped. Boxes and containers have ids unique in the diagram; a line's ends are boxes or containers.
/// </summary>
public sealed record PictureDiagram(IReadOnlyList<DiagramBox> Boxes, IReadOnlyList<DiagramContainer> Containers, IReadOnlyList<DiagramLine> Lines, IReadOnlyList<DiagramText> Texts)
{
    /// <summary>The diagram's title as written on it, or null. It is also among <see cref="Texts"/>, as a text of kind <see cref="DiagramTextKind.Title"/>.</summary>
    public string? Title { get; init; }
}

/// <summary>The shape a box was drawn as; <see cref="Box"/> when the AI named another or none.</summary>
public enum DiagramShape { Box, Ellipse, Cylinder, Note }

/// <summary>A line's arrow heads: none, at <see cref="DiagramLine.To"/> only, or at both ends.</summary>
public enum DiagramArrow { None, Forward, Both }

/// <summary>Where on the picture, as fractions of its width and height (0 to 1, from the top left). Inside the picture.</summary>
public sealed record DiagramArea(double X, double Y, double Width, double Height);

/// <summary>
/// A box: its label, a short second line when it has one (<paramref name="Detail"/>), its shape, the container it sits in (an id of
/// <see cref="PictureDiagram.Containers"/>, or null), and where it is when the AI gave it.
/// </summary>
public sealed record DiagramBox(string Id, string Label, string? Detail, DiagramShape Shape, string? Container, DiagramArea? Area);

/// <summary>A container that encloses boxes or other containers: its label, the container it sits in (or null), and where it is.</summary>
public sealed record DiagramContainer(string Id, string Label, string? Container, DiagramArea? Area);

/// <summary>A line between two boxes or containers (ids), with its label, arrow heads, and whether it is dashed or drawn thick.</summary>
public sealed record DiagramLine(string From, string To, string? Label, DiagramArrow Arrow, bool Dashed, bool Thick);

/// <summary>Text on the picture that belongs to no box: a title, a note, a to-do line, a remark beside something. It may sit in a container.</summary>
public sealed record DiagramText(string Text, string? Container, DiagramArea? Area)
{
    /// <summary>What the text is; <see cref="DiagramTextKind.Note"/> when the AI said nothing it knows.</summary>
    public DiagramTextKind Kind { get; init; }

    /// <summary>For a remark: the id of the box or container it is written beside, or null.</summary>
    public string? Beside { get; init; }
}

/// <summary>A title of the diagram, a note on it (a to-do line, a legend), or a remark written beside one box or container.</summary>
public enum DiagramTextKind { Note, Title, Remark }

/// <summary>0.10.0. What a <see cref="PictureDiagram"/> holds at most. Longer labels are cut; items over the counts are dropped.</summary>
public static class DiagramLimits
{
    public const int Boxes = 150, Containers = 40, Lines = 200, Texts = 40, Depth = 5, Label = 100, Detail = 100, Text = 300, Id = 32;
}

/// <summary>Where on a page of the draft a <see cref="DraftAddition"/> goes.</summary>
public enum DraftPlace
{
    /// <summary>After the page's last block.</summary>
    End,
    /// <summary>Before the page's first block.</summary>
    Start,
    /// <summary>
    /// Before the first heading that has a block before it: after an opening paragraph, or after a first section that starts with a
    /// heading. At the end when the page has no such heading.
    /// </summary>
    AfterIntroduction,
    /// <summary>
    /// 0.10.0. Right after the picture whose image node has <see cref="DraftAddition.Picture"/> as its <c>attrs.src</c>
    /// (<see cref="DraftPicture.Src"/>). At the end when the page does not show that picture.
    /// </summary>
    AfterPicture,
}

/// <summary>
/// Editor nodes to add to page <paramref name="Page"/> (0 is the first) of the draft, at <paramref name="Place"/>, in this order.
/// Each is the stored JSON of one block node, as a page holds it.
/// </summary>
public sealed record DraftAddition(int Page, DraftPlace Place, IReadOnlyList<JsonElement> Nodes)
{
    /// <summary>0.10.0. For <see cref="DraftPlace.AfterPicture"/>: the picture's <see cref="DraftPicture.Src"/>.</summary>
    public string? Picture { get; init; }
}

/// <summary>What a draft contributor adds. All the additions together: at most 50 nodes and 512 KB of JSON.</summary>
public sealed record DraftContribution(IReadOnlyList<DraftAddition> Additions);

/// <summary>
/// Reads the draft of an AI import item and adds content of its own to it, before the importer reviews and accepts it: content built
/// from the text the AI drafted. It is called once per item the AI drafted, after the <see cref="IImportContributor"/>s, in its own
/// scope, as its plugin (an actor of kind Plugin, source "import"). It is not called for an item that holds a secret or that goes to a
/// sensitive space. A call that throws, runs past 10 seconds, names a page the draft does not have, or answers more than the limits of
/// <see cref="DraftContribution"/> adds nothing and counts as a failure of the plugin; the import goes on.
/// </summary>
public interface IDraftContributor
{
    /// <summary>Null, or nodes to add, for this draft.</summary>
    Task<DraftContribution?> ContributeAsync(ImportDraft draft, CancellationToken ct);
}

public sealed record DraftContributorReg(Type Contributor);

/// <summary>
/// 0.11.0. A tool a plugin offers the AI while an import is drafted: its <paramref name="Name"/>, a short
/// <paramref name="Description"/> of when to use it, and the JSON schema of its input (<paramref name="InputSchema"/>). These three are
/// the only text of a plugin that reaches the AI. The app checks them before it offers the tool (see the plugin guide for the rules) and
/// does not offer a tool that breaks one; the Admin sees each tool on the plugin's page. <paramref name="Tool"/> answers the calls.
/// </summary>
public sealed record ImportToolReg(string Name, string Description, JsonElement InputSchema, Type Tool)
{
    /// <summary>
    /// 0.11.0. What in a document the tool is for, as line patterns the app checks, caps and runs on the source itself (see
    /// <see cref="ToolRegions"/>). A tool with regions is offered only for documents that hold one, takes one result for each region, and
    /// its input is held to the layout of the region (labels stacked in one enclosure, text given together written together). Null: the
    /// tool is for any document.
    /// </summary>
    public ToolRegions? For { get; init; }

}

/// <summary>
/// 0.11.0. The regions of a document a tool is for: runs of lines found by the app from the source alone, never by the model or
/// the plugin. A region starts at a line that matches one of <paramref name="Starts"/> and goes on over lines that match
/// <paramref name="Starts"/> or <paramref name="Joins"/>, with at most <paramref name="MaxGap"/> other lines in a row. It counts when it has
/// at least <paramref name="MinLines"/> such lines and <paramref name="MinStarts"/> of them match <paramref name="Starts"/> but none of
/// <paramref name="Weak"/>, or when one of its lines matches <paramref name="Alone"/>. Markdown table rows are never part of a region.
/// Each pattern is a .NET regular expression, run without backtracking and with a time limit; at most 16 in each list and 300 characters
/// each; <paramref name="MinLines"/> 1 to 20, <paramref name="MinStarts"/> 0 to 20, <paramref name="MaxGap"/> 0 to 5. A tool whose regions
/// break a rule is not offered.
/// </summary>
public sealed record ToolRegions(IReadOnlyList<string> Starts, IReadOnlyList<string> Joins, IReadOnlyList<string> Weak, IReadOnlyList<string> Alone,
    int MinLines = 3, int MinStarts = 2, int MaxGap = 2)
{
}

/// <summary>
/// 0.11.0. Answers the AI's calls of one tool during an import. It gets only the input of the call, checked against the tool's
/// schema; never the document, the prompt or the rest of the conversation. It is made for each call, in its own scope, as its plugin
/// (an actor of kind Plugin, source "import"). A call that throws, runs past 10 seconds, or answers more than the limits of
/// <see cref="DraftContribution"/> adds nothing and counts as a failure of the plugin; the import goes on.
/// </summary>
public interface IImportTool
{
    /// <summary>Null (nothing to add), or the nodes to add to the draft for this call. The app places them where the AI said.</summary>
    Task<ImportToolResult?> CallAsync(JsonElement input, CancellationToken ct);
}

/// <summary>
/// 0.11.0. Editor nodes to add to the draft, each the stored JSON of one block node. At most 50 nodes and 512 KB of JSON. Or a
/// refusal (<see cref="Refuse"/>): no nodes, a <see cref="Refusal"/> from the app's fixed list and, for some, <see cref="At"/>, the path in
/// the input it is about. The app words the reply to the model itself; no text of the plugin reaches the model.
/// </summary>
public sealed record ImportToolResult(IReadOnlyList<JsonElement> Nodes)
{
    /// <summary>Why the tool added nothing, from the app's list; <see cref="ImportToolRefusal.None"/> for a result.</summary>
    public ImportToolRefusal Refusal { get; init; }

    /// <summary>
    /// The place in the input the refusal is about, as a JSON path into the call's input ("$.boxes[3].group"): a dollar sign, then names
    /// of letters, digits and underscores after dots and numbers in brackets, at most 12 steps. A path that is not of that form, or that
    /// names nothing in the input, is left out of the reply.
    /// </summary>
    public string? At { get; init; }

    /// <summary>A refusal: nothing is added, and the model is told why in the app's words.</summary>
    public static ImportToolResult Refuse(ImportToolRefusal why, string? at = null) => new([]) { Refusal = why, At = at };
}

/// <summary>0.11.0. Why a tool added nothing. The app owns and words each one.</summary>
public enum ImportToolRefusal
{
    /// <summary>Not a refusal.</summary>
    None,
    /// <summary>The input names an id it does not declare (a line to a box that is not there, a box in a group that is not there).</summary>
    UnknownId,
    /// <summary>The input declares the same id twice.</summary>
    DuplicateId,
    /// <summary>The input holds too little to make anything of.</summary>
    TooLittle,
    /// <summary>The input holds more than the tool can make.</summary>
    TooMuch,
    /// <summary>An item of the input refers to itself (a line from a box to the same box).</summary>
    SelfReference,
    /// <summary>One text of the input holds several items that are each one of their own (things in brackets listed in one box's detail).</summary>
    Combined,
}

public static class ImportToolExtensions
{
    /// <summary>
    /// 0.11.0. Registers a tool the AI may call while an import is drafted. <paramref name="inputSchema"/> is the JSON schema of
    /// its input, as text. Throws when it is not JSON; the app checks the rest before it offers the tool.
    /// </summary>
    public static IPluginApp ImportTool<TTool>(this IPluginApp app, string name, string description, string inputSchema) where TTool : class, IImportTool
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSchema);
        using var schema = JsonDocument.Parse(inputSchema);
        return app.Add(new ImportToolReg(name, description, schema.RootElement.Clone(), typeof(TTool)));
    }

    /// <summary>
    /// 0.11.0. Registers a tool, as <see cref="ImportTool{TTool}"/>, for the regions of a document that <paramref name="regions"/>
    /// describes. The app offers it only when the documents hold such a region, as the app finds it, and takes one result of it for each.
    /// </summary>
    public static IPluginApp ImportRegionTool<TTool>(this IPluginApp app, string name, string description, string inputSchema, ToolRegions regions)
        where TTool : class, IImportTool
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(regions);
        using var schema = JsonDocument.Parse(inputSchema);
        return app.Add(new ImportToolReg(name, description, schema.RootElement.Clone(), typeof(TTool)) { For = regions });
    }

}
