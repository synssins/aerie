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
public sealed record ImportDraft(Guid ImportItemId, IReadOnlyList<DraftPage> Pages, IReadOnlyList<string> Files);

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
}

/// <summary>
/// Editor nodes to add to page <paramref name="Page"/> (0 is the first) of the draft, at <paramref name="Place"/>, in this order.
/// Each is the stored JSON of one block node, as a page holds it.
/// </summary>
public sealed record DraftAddition(int Page, DraftPlace Place, IReadOnlyList<JsonElement> Nodes);

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
