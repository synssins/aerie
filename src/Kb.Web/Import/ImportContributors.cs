using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kb.Core.Audit;
using Kb.Core.Import;
using Kb.Core.Plugins;
using Kb.Web.Pipeline;

namespace Kb.Web.Import;

/// <summary>What one plugin added to a draft from one file.</summary>
public sealed record ImportContributed(string PluginId, string PluginName, string FileName, IReadOnlyList<JsonNode> Nodes);

/// <summary>
/// Runs the plugins' import contributors over the files of an item, for its draft. The files have passed every virus scanner.
/// Each call runs in its own scope as its plugin (source "import"), on its own read-only stream, within a time limit; what it answers
/// is checked (block nodes, within the size limits) and copied. A call that fails adds nothing, is counted against its plugin, and
/// never fails the import. Every contribution is audited with the plugin's id.
/// </summary>
public sealed class ImportContributors(IServiceScopeFactory scopes, IRegistrations registrations, PluginRegistry registry, IPluginFailures failures,
    IAuditLog audit, ILogger<ImportContributors> log)
{
    public const int MaxNodes = 50, MaxBytes = 512 * 1024;
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);
    /// <summary>A test sets a shorter limit.</summary>
    internal TimeSpan? Limit { get; set; }

    public bool Any => registrations.All<ImportContributorReg>().Count > 0;

    public async Task<IReadOnlyList<ImportContributed>> RunAsync(ImportItem item, IReadOnlyList<ImportFile> files, Func<Guid, CancellationToken, Task<byte[]>> read,
        CancellationToken ct)
    {
        var found = new List<ImportContributed>();
        var contributors = registrations.All<ImportContributorReg>();
        if (contributors.Count == 0) return found;
        foreach (var f in files)
        {
            var data = await read(f.Id, ct);
            var file = new ImportedFile(item.Id, f.Path, f.Name, data.Length, _ => Task.FromResult<Stream>(new MemoryStream(data, false)));
            foreach (var c in contributors)
            {
                var nodes = await CallAsync(c, file, item, ct);
                if (nodes is not { Count: > 0 }) continue;
                var name = registry.Find(c.PluginId) is { } p ? PluginManifests.NameOf(p.Manifest) : c.PluginId;
                found.Add(new ImportContributed(c.PluginId, name, f.Name, nodes));
                await audit.AppendAsync(new AuditEvent(AuditActions.ImportContribute, item.UserId, "importItem", item.Id.ToString(), Data: new Dictionary<string, string>
                {
                    ["plugin"] = c.PluginId, ["file"] = f.Path, ["sha256"] = f.Sha256, ["nodes"] = nodes.Count.ToString(CultureInfo.InvariantCulture),
                }), ct);
            }
        }
        return found;
    }

    private async Task<IReadOnlyList<JsonNode>?> CallAsync(Registered<ImportContributorReg> c, ImportedFile file, ImportItem item, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActorAccessor>().Use(Actor.Plugin(c.PluginId, ActorSources.Import).ForImport(item.Id));
            var run = ((IImportContributor)ActivatorUtilities.CreateInstance(scope.ServiceProvider, c.Value.Contributor)).ContributeAsync(file, stop.Token);
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // a call given up may still fail later
            var answer = await run.WaitAsync(Limit ?? TimeLimit, ct);
            return answer is null ? null : Check(answer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (e is TimeoutException) await stop.CancelAsync();
            log.LogWarning(e, "Import contributor of {Plugin} failed on {File}.", c.PluginId, file.Path);
            failures.Report(c.PluginId, "import contributor", e);
            return null;
        }
    }

    /// <summary>
    /// The nodes, copied out of the plugin's own JSON, when each is a block node (an object with a type that is not "doc" or "text") and
    /// the whole is within the limits. Throws otherwise: an answer the core cannot use is the plugin failing.
    /// </summary>
    public static IReadOnlyList<JsonNode> Check(ImportContribution answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Nodes is null) throw new InvalidDataException("The contribution has no node list.");
        if (answer.Nodes.Count > MaxNodes) throw new InvalidDataException($"More than {MaxNodes} nodes.");
        var nodes = new List<JsonNode>();
        var size = 0;
        foreach (var n in answer.Nodes)
        {
            if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 } type
                || type is "doc" or "text")
                throw new InvalidDataException("A node is not a block node of the editor.");
            var raw = n.GetRawText();
            if ((size += raw.Length) > MaxBytes) throw new InvalidDataException($"The nodes are over {MaxBytes / 1024} KB.");
            nodes.Add(JsonNode.Parse(raw)!);
        }
        return nodes;
    }
}
