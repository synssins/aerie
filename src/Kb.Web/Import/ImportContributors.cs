using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kb.Core.Audit;
using Kb.Core.Import;
using Kb.Core.Plugins;
using Kb.Web.Pipeline;

namespace Kb.Web.Import;

/// <summary>What one plugin added to a draft from one file.</summary>
public sealed record ImportContributed(string PluginId, string PluginName, string FileName, IReadOnlyList<JsonNode> Nodes);

/// <summary>Checked nodes for one place on one page of the draft; <paramref name="Picture"/> for <see cref="DraftPlace.AfterPicture"/>.</summary>
public sealed record DraftNodes(int Page, DraftPlace Place, IReadOnlyList<JsonNode> Nodes, string? Picture = null);

/// <summary>What one plugin added to a draft from the draft itself.</summary>
public sealed record DraftContributed(string PluginId, string PluginName, IReadOnlyList<DraftNodes> Additions)
{
    public int Count => Additions.Sum(a => a.Nodes.Count);
}

/// <summary>
/// Runs the plugins' import contributors over the files of an item, and their draft contributors over its draft. The files have passed
/// every virus scanner. Each call runs in its own scope as its plugin (source "import"), within a time limit; what it answers is checked
/// (block nodes, within the size limits) and copied. A call that fails adds nothing, is counted against its plugin, and never fails the
/// import. Every contribution is audited with the plugin's id.
/// </summary>
public sealed class ImportContributors(IServiceScopeFactory scopes, IRegistrations registrations, PluginRegistry registry, IPluginFailures failures,
    IAuditLog audit, ILogger<ImportContributors> log)
{
    public const int MaxNodes = 50, MaxBytes = 512 * 1024;
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);
    /// <summary>A test sets a shorter limit.</summary>
    internal TimeSpan? Limit { get; set; }

    public bool Any => registrations.All<ImportContributorReg>().Count > 0 || registrations.All<DraftContributorReg>().Count > 0;

    /// <summary>Some plugin reads drafts: only then are the pictures of an import read for diagrams.</summary>
    public bool AnyDraft => registrations.All<DraftContributorReg>().Count > 0;

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
                var nodes = await CallAsync(c.PluginId, item, "import contributor", file.Path,
                    (sp, t) => ((IImportContributor)ActivatorUtilities.CreateInstance(sp, c.Value.Contributor)).ContributeAsync(file, t), Check, ct);
                if (nodes is not { Count: > 0 }) continue;
                found.Add(new ImportContributed(c.PluginId, NameOf(c.PluginId), f.Name, nodes));
                await audit.AppendAsync(new AuditEvent(AuditActions.ImportContribute, item.UserId, "importItem", item.Id.ToString(), Data: new Dictionary<string, string>
                {
                    ["plugin"] = c.PluginId, ["file"] = f.Path, ["sha256"] = f.Sha256, ["nodes"] = nodes.Count.ToString(CultureInfo.InvariantCulture),
                }), ct);
            }
        }
        return found;
    }

    /// <summary>
    /// Runs the draft contributors, after the file contributors, in the order the plugins registered them. Each sees the pages as the
    /// ones before it left them (<paramref name="pages"/> is asked again for each); what it answers is checked and then placed.
    /// </summary>
    public async Task<IReadOnlyList<DraftContributed>> RunDraftAsync(ImportItem item, Func<IReadOnlyList<DraftPage>> pages, IReadOnlyList<string> files,
        Action<DraftContributed> place, CancellationToken ct, IReadOnlyList<DraftPicture>? pictures = null)
    {
        var found = new List<DraftContributed>();
        foreach (var c in registrations.All<DraftContributorReg>())
        {
            var draft = new ImportDraft(item.Id, pages(), files) { Pictures = pictures ?? [] };
            var additions = await CallAsync(c.PluginId, item, "draft contributor", "the draft",
                (sp, t) => ((IDraftContributor)ActivatorUtilities.CreateInstance(sp, c.Value.Contributor)).ContributeAsync(draft, t), a => Check(a, draft.Pages.Count), ct);
            if (additions is null) continue;
            var done = new DraftContributed(c.PluginId, NameOf(c.PluginId), additions);
            if (done.Count == 0) continue;
            place(done);
            found.Add(done);
            await audit.AppendAsync(new AuditEvent(AuditActions.ImportContribute, item.UserId, "importItem", item.Id.ToString(), Data: new Dictionary<string, string>
            {
                ["plugin"] = c.PluginId, ["pages"] = string.Join(",", additions.Select(a => a.Page).Distinct()), ["nodes"] = done.Count.ToString(CultureInfo.InvariantCulture),
            }), ct);
        }
        return found;
    }

    /// <summary>
    /// The loaded plugins that claim this file's kind (<see cref="ImportFileKindReg"/>), with their names; none for a kind the app reads
    /// itself. A disabled plugin is not loaded, so its claim does not count.
    /// </summary>
    public IReadOnlyList<(string Id, string Name)> ClaimersOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var claims = registrations.All<ImportFileKindReg>().Where(r => r.Value.Extensions.Contains(ext)).Select(r => r.PluginId).Distinct().ToList();
        return claims.Count == 0 || Extractors.Reads(fileName) ? [] : [.. claims.Select(id => (id, NameOf(id)))];
    }

    private string NameOf(string pluginId) => registry.Find(pluginId) is { } p ? PluginManifests.NameOf(p.Manifest) : pluginId;

    /// <summary>
    /// One call of a plugin, in its own scope, as the plugin (source "import"), within the time limit. At the limit the core stops waiting
    /// and cancels the token; the call may run on, and what it answers then is dropped. A failure is logged and counted against the plugin.
    /// </summary>
    private async Task<TResult?> CallAsync<TAnswer, TResult>(string pluginId, ImportItem item, string where, string what,
        Func<IServiceProvider, CancellationToken, Task<TAnswer?>> call, Func<TAnswer, TResult> check, CancellationToken ct)
        where TAnswer : class where TResult : class
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActorAccessor>().Use(Actor.Plugin(pluginId, ActorSources.Import).ForImport(item.Id));
            var run = call(scope.ServiceProvider, stop.Token);
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // a call given up may still fail later
            var answer = await run.WaitAsync(Limit ?? TimeLimit, ct);
            return answer is null ? null : check(answer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            if (e is TimeoutException) await stop.CancelAsync();
            log.LogWarning(e, "The {Where} of {Plugin} failed on {What}.", where, pluginId, what);
            // A file a contributor is slow on is the file's doing as much as the plugin's: it is skipped, and does not count toward turning the plugin off.
            if (e is not TimeoutException) failures.Report(pluginId, where, e);
            return null;
        }
    }

    /// <summary>
    /// The nodes, copied out of the plugin's own JSON, when each is a block node (an object with a type that is not "doc" or "text") and
    /// the whole is within the limits: at most <see cref="MaxNodes"/> nodes, and <see cref="MaxBytes"/> of their JSON as the plugin gave
    /// it, counted in UTF-8 bytes and summed over the nodes. Throws otherwise: an answer the core cannot use is the plugin failing, and
    /// none of it is kept.
    /// </summary>
    public static IReadOnlyList<JsonNode> Check(ImportContribution answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Nodes is null) throw new InvalidDataException("The contribution has no node list.");
        var (count, size) = (0, 0);
        return Copy(answer.Nodes, ref count, ref size);
    }

    /// <summary>A draft contributor's additions, checked as <see cref="Check(ImportContribution)"/> over all of them together, each on a page the draft has.</summary>
    public static IReadOnlyList<DraftNodes> Check(DraftContribution answer, int pages)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Additions is null) throw new InvalidDataException("The contribution has no list of additions.");
        var (count, size) = (0, 0);
        var found = new List<DraftNodes>();
        foreach (var a in answer.Additions)
        {
            if (a?.Nodes is null) throw new InvalidDataException("An addition has no node list.");
            if (a.Page < 0 || a.Page >= pages) throw new InvalidDataException($"The draft has no page {a.Page}.");
            if (!Enum.IsDefined(a.Place)) throw new InvalidDataException("An addition has no known place.");
            found.Add(new DraftNodes(a.Page, a.Place, Copy(a.Nodes, ref count, ref size), a.Place == DraftPlace.AfterPicture ? a.Picture : null));
        }
        return found;
    }

    private static List<JsonNode> Copy(IReadOnlyList<JsonElement> answer, ref int count, ref int size)
    {
        if ((count += answer.Count) > MaxNodes) throw new InvalidDataException($"More than {MaxNodes} nodes.");
        var nodes = new List<JsonNode>();
        foreach (var n in answer)
        {
            if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 } type
                || type is "doc" or "text")
                throw new InvalidDataException("A node is not a block node of the editor.");
            var raw = n.GetRawText();
            if ((size += Encoding.UTF8.GetByteCount(raw)) > MaxBytes) throw new InvalidDataException($"The nodes are over {MaxBytes / 1024} KB.");
            nodes.Add(JsonNode.Parse(raw)!);
        }
        return nodes;
    }

    /// <summary>
    /// Puts each addition on its page (<paramref name="pages"/> are the pages' content arrays): at the start, before the first heading that
    /// has a block before it (the end when there is none), right after a picture (the end when the page does not show it), or at the end.
    /// Additions to one place keep their order.
    /// </summary>
    public static void Place(IReadOnlyList<JsonArray> pages, IReadOnlyList<DraftNodes> additions)
    {
        foreach (var page in additions.GroupBy(a => a.Page))
        {
            var content = pages[page.Key];
            var old = content.ToList();
            content.Clear();
            var intro = old.Count < 2 ? -1 : old.FindIndex(1, n => n?["type"]?.GetValue<string>() == "heading");
            if (intro < 0) intro = old.Count;
            // The picture's block: the top-level block that is the image, or holds it (an image inside a list item).
            int PictureAt(string? src) => src is null ? -1 : old.FindIndex(n => n is not null && Shows(n, src));
            var after = page.Where(a => a.Place == DraftPlace.AfterPicture).Select(a => (At: PictureAt(a.Picture), a.Nodes)).ToList();
            IEnumerable<JsonNode?> At(DraftPlace p) => page.Where(a => a.Place == p).SelectMany(a => a.Nodes);
            IEnumerable<JsonNode?> AfterBlock(int i) => after.Where(a => a.At == i).SelectMany(a => a.Nodes);
            IEnumerable<JsonNode?> Blocks(IEnumerable<(JsonNode? Node, int Index)> from) => from.SelectMany(b => new[] { b.Node }.Concat(AfterBlock(b.Index)));
            var indexed = old.Select((n, i) => (n, i)).ToList();
            foreach (var n in At(DraftPlace.Start).Concat(Blocks(indexed.Take(intro))).Concat(At(DraftPlace.AfterIntroduction)).Concat(Blocks(indexed.Skip(intro)))
                .Concat(At(DraftPlace.End)).Concat(AfterBlock(-1)))
                content.Add(n);
        }
    }

    /// <summary>Whether the node is, or holds, an image with this src.</summary>
    public static bool Shows(JsonNode node, string src)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node["type"]?.GetValue<string>() == "image" && node["attrs"]?["src"]?.GetValue<string>() == src) return true;
        return node["content"] is JsonArray children && children.Any(c => c is not null && Shows(c, src));
    }
}
