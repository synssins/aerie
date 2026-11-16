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
}
