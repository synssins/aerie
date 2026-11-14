using System.Reflection;
using System.Runtime.Loader;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Data.Plugins;

/// <summary>
/// What starting the plugins gave: the registry, what they registered, the audit events to write once the app is up,
/// and the services they asked for, kept aside until <see cref="PluginBootstrap.Seal"/>.
/// </summary>
public sealed record PluginBoot(PluginRegistry Registry, IRegistrations Registrations, IReadOnlyList<AuditEvent> Events)
{
    public IReadOnlyList<ServiceDescriptor> PluginServices { get; init; } = [];
}

/// <summary>
/// Runs before the web host is built: migrates the database, reads the plugins that are enabled, unpacks and loads them,
/// and runs each one against scratch lists. A plugin that fails anywhere is disabled by the core with the reason, and the app starts without it.
/// </summary>
public static class PluginBootstrap
{
    /// <summary>The assemblies whose actions a plugin may not hook, filter, follow or handle.</summary>
    private static readonly string[] CoreAssemblies = ["Kb.Web", "Kb.Core", "Kb.Data"];

    private sealed record Candidate(PluginManifest Manifest, PluginOrigin Origin, PluginStatus? Status, string Source, Assembly? Assembly, string? Sha256);

    public static async Task<PluginBoot> RunAsync(IServiceCollection services, IConfiguration config, KbOptions options, string contentRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = config; // plugin settings are in the database; configuration is no longer read for them
        var builder = new DbContextOptionsBuilder<KbDbContext>();
        _ = options.Database.Provider switch
        {
            DatabaseProvider.Sqlite => builder.UseSqlite(options.Database.ConnectionString, s => s.MigrationsAssembly(KbDbContext.SqliteMigrations)),
            _ => builder.UseSqlServer(options.Database.ConnectionString, s => s.EnableRetryOnFailure()),
        };
        await using var db = new KbDbContext(builder.Options);
        await db.Database.MigrateAsync(ct); // both providers: SQLite and SQL Server each have their own migrations

        var safeMode = options.Plugins.SafeMode;
        var now = DateTimeOffset.UtcNow;
        // The generation is read before the rows it guards. If another server changes them in between, this server holds an old number
        // with new rows and restarts once more; read the other way round it would hold a new number with old rows and never restart.
        var generation = long.TryParse((await db.RuntimeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == RuntimeSettingKeys.PluginGeneration, ct))?.Value, out var g) ? g : 0;
        var statuses = (await db.PluginStatuses.ToListAsync(ct)).ToDictionary(s => s.PluginId, StringComparer.Ordinal);
        var settings = new PluginSettingsSnapshot();
        settings.Replace((await db.PluginSettingValues.AsNoTracking().Where(v => !v.Secret).ToListAsync(ct)).Select(v => (v.PluginId, v.Key, v.Value)));

        // ---- what exists
        var candidates = new List<Candidate>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal); // id -> reason
        var packages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (manifest, assembly) in PluginRuntime.BuiltIns(AppContext.BaseDirectory))
        {
            if (!statuses.TryGetValue(manifest.Id, out var status) && !safeMode)
            {
                status = new PluginStatus { PluginId = manifest.Id, State = PluginState.Enabled, UpdatedAt = now };
                db.PluginStatuses.Add(status);
                statuses[manifest.Id] = status;
            }
            candidates.Add(new Candidate(manifest, PluginOrigin.BuiltIn, status, "built-in", assembly, null));
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another server, starting at the same moment, added the same rows first. Its rows are the ones to use.
            foreach (var entry in db.ChangeTracker.Entries<PluginStatus>().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
            statuses = (await db.PluginStatuses.ToListAsync(ct)).ToDictionary(s => s.PluginId, StringComparer.Ordinal);
            candidates = [.. candidates.Select(c => c with { Status = statuses.GetValueOrDefault(c.Manifest.Id) })];
        }
        foreach (var status in statuses.Values.Where(s => s.ActiveVersion is not null && !s.Removed).OrderBy(s => s.PluginId, StringComparer.Ordinal).ToList())
        {
            if (candidates.Any(c => c.Manifest.Id == status.PluginId)) continue; // a built-in plugin keeps its id
            var load = status.State == PluginState.Enabled && !safeMode;
            var package = await db.PluginPackages.AsNoTracking().Where(p => p.PluginId == status.PluginId && p.Version == status.ActiveVersion)
                .Select(p => new { p.ManifestJson, p.Source, p.Sha256, Bytes = load ? p.Bytes : null }).FirstOrDefaultAsync(ct);
            if (package is null && load)
            {
                // No package for the version the status names. If another server is updating or removing this plugin right now, the row has changed
                // or is gone: leave it alone, the generation it raises restarts this server. Only a row that still names the missing package is broken.
                var fresh = await db.PluginStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.PluginId == status.PluginId, ct);
                if (fresh is null || fresh.ActiveVersion != status.ActiveVersion) continue;
            }
            var manifest = (package is null ? null : PluginManifests.Parse(package.ManifestJson)) ?? new PluginManifest { Id = status.PluginId, Version = status.ActiveVersion!, Publisher = "" };
            if (load)
            {
                // The stored bytes are checked again: they are what gets unpacked and run.
                var read = package?.Bytes is null ? new Problem(400, "The package is missing.") : PluginPackageReader.Read(package.Bytes);
                if (!read.Ok) failed[status.PluginId] = read.Problem!.Message ?? "Not a plugin package.";
                else if (read.Value!.Manifest.Id != status.PluginId || read.Value.Manifest.Version != status.ActiveVersion) failed[status.PluginId] = "Not a plugin package.";
                else { manifest = read.Value.Manifest; packages[status.PluginId] = package!.Bytes!; }
            }
            candidates.Add(new Candidate(manifest, PluginOrigin.Uploaded, status, package?.Source ?? "upload", null, package?.Sha256));
        }

        // ---- load what is enabled, requirements first
        var events = new List<AuditEvent>();
        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, AssemblyLoadContext>(StringComparer.Ordinal);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var registrations = new PluginRegistrations();
        var pluginServices = new List<ServiceDescriptor>();
        var handledActions = new HashSet<Type>();
        var renderedNodes = new HashSet<string>(StringComparer.Ordinal);
        var enabled = candidates.Where(c => (c.Status?.State ?? PluginState.Enabled) == PluginState.Enabled && (c.Origin == PluginOrigin.BuiltIn || !safeMode)).ToList();
        var byId = enabled.ToDictionary(c => c.Manifest.Id, StringComparer.Ordinal);
        var work = WorkFolder(options.Plugins.WorkFolder, contentRoot);

        void Fail(Candidate c, string reason)
        {
            reason = reason.Split('\r', '\n')[0];
            reason = reason[..Math.Min(reason.Length, 300)];
            failed[c.Manifest.Id] = reason;
            if (c.Status is null) return; // Safe Mode writes no state
            c.Status.State = PluginState.DisabledByCore;
            c.Status.Reason = reason;
            c.Status.UpdatedAt = now;
            if (c.Manifest.Enforcing) c.Status.LockedAt = now;
            events.Add(new AuditEvent(AuditActions.PluginDisabledByCore, null, "plugin", c.Manifest.Id,
                Data: new Dictionary<string, string> { ["reason"] = reason, ["version"] = c.Manifest.Version, ["sha256"] = c.Sha256 ?? "", ["source"] = c.Source }));
        }

        foreach (var c in enabled.Where(c => failed.ContainsKey(c.Manifest.Id)).ToList()) Fail(c, failed[c.Manifest.Id]);
        var plan = PluginGraph.Order([.. enabled.Select(c => c.Manifest)]);
        foreach (var (id, reason) in plan.Refused) if (!failed.ContainsKey(id)) Fail(byId[id], reason);
        foreach (var id in plan.LoadOrder)
        {
            var c = byId[id];
            if (failed.ContainsKey(id)) continue;
            if (PluginVersions.Refusal(c.Manifest) is { } refusal) { Fail(c, refusal); continue; }
            if (c.Manifest.Requires.FirstOrDefault(r => failed.ContainsKey(r.Id)) is { } broken) { Fail(c, $"Requires {PluginManifests.NameOf(byId[broken.Id].Manifest)}."); continue; }
            try
            {
                var assembly = c.Assembly;
                if (assembly is null)
                {
                    var folder = Unpacked(work, c.Manifest, c.Sha256!, packages[id]);
                    // Only what a required plugin shares is visible to this one.
                    var exports = c.Manifest.Requires.Where(r => contexts.ContainsKey(r.Id)).SelectMany(r => byId[r.Id].Manifest.Exports.Select(e => (Name: e, Owner: contexts[r.Id])))
                        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(e => e.Key, e => e.First().Owner, StringComparer.OrdinalIgnoreCase);
                    assembly = PluginRuntime.LoadUploaded(c.Manifest, folder, exports, out var context);
                    contexts[id] = context;
                    folders[id] = folder;
                }
                var configured = PluginRuntime.Configure(assembly, c.Manifest, settings);
                // Only the actions of the contract assembly, and of plugins, are open to plugins. The core's own (API tokens, plugin and
                // storage administration and the like) carry secrets or decide about plugins themselves.
                foreach (var action in configured.Registrations.Select(r => r switch
                         {
                             Kb.Plugins.Actions.BeforeHook b => b.Action, Kb.Plugins.Actions.ResultFilter f => f.Action,
                             Kb.Plugins.Actions.AfterHandler a => a.Action, Kb.Plugins.Actions.ActionHandler h => h.Action, _ => null,
                         }).OfType<Type>())
                {
                    if (CoreAssemblies.Contains(action.Assembly.GetName().Name ?? "", StringComparer.Ordinal))
                        throw new InvalidOperationException($"{action.GetCustomAttribute<Kb.Plugins.Actions.ActionAttribute>()?.Name ?? action.FullName} is not open to plugins.");
                }
                // An action has one handler. Every action of the contract assembly has the core's; a plugin's own action has its plugin's.
                foreach (var handled in configured.Registrations.OfType<Kb.Plugins.Actions.ActionHandler>())
                {
                    if (handled.Action.Assembly == typeof(Kb.Plugins.Actions.IAction<>).Assembly || !handledActions.Add(handled.Action))
                        throw new InvalidOperationException($"{handled.Action.GetCustomAttribute<Kb.Plugins.Actions.ActionAttribute>()?.Name ?? handled.Action.FullName} already has a handler.");
                }
                // An editor node type has one renderer, and the editor's own types have none.
                var nodes = configured.Registrations.OfType<NodeRenderer>().Select(n => n.Type).ToList();
                if (nodes.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 100)) throw new InvalidOperationException("An editor node type is 1 to 100 characters.");
                if (nodes.FirstOrDefault(Kb.Core.Content.DocText.CoreNodes.Contains) is { } core) throw new InvalidOperationException($"{core} is a node of the editor.");
                if (nodes.FirstOrDefault(n => renderedNodes.Contains(n) || nodes.Count(x => x == n) > 1) is { } taken) throw new InvalidOperationException($"{taken} already has a renderer.");
                if (c.Manifest.Schedules.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100)) throw new InvalidOperationException("A schedule name is 1 to 100 characters.");
                // Every schedule of the manifest names a job the plugin registered, and every job has a schedule.
                var jobs = configured.Registrations.OfType<JobReg>().Select(j => j.Name).ToList();
                if (c.Manifest.Schedules.FirstOrDefault(s => !jobs.Contains(s.Name, StringComparer.Ordinal)) is { } noJob) throw new InvalidOperationException($"No job named {noJob.Name}.");
                if (jobs.FirstOrDefault(j => c.Manifest.Schedules.All(s => s.Name != j)) is { } noSchedule) throw new InvalidOperationException($"No schedule named {noSchedule}.");
                if (c.Manifest.Schedules.FirstOrDefault(s => s.EveryMinutes < 1) is { } tooOften) throw new InvalidOperationException($"Schedule {tooOften.Name}: everyMinutes must be 1 or more.");
                // Its tables are made ready before anything of it is kept: a plugin whose tables cannot be made is not loaded.
                foreach (var data in configured.Registrations.OfType<Kb.Plugins.Data.PluginData>())
                {
                    var (context, dataOptions) = (data.Context, PluginDataHost.Options(id, options));
                    await PluginDataHost.ReadyAsync(context, dataOptions, ct);
                    pluginServices.Add(ServiceDescriptor.Scoped(context, _ => PluginDataHost.Create(context, dataOptions)));
                }
                pluginServices.AddRange(configured.Services);
                foreach (var registration in configured.Registrations) registrations.Add(id, registration);
                renderedNodes.UnionWith(nodes);
                loaded[id] = assembly;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested)) // a plugin whose HTTP call times out throws a cancellation too
            {
                Fail(c, (e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message);
            }
        }
        if (!safeMode)
        {
            // What each loaded plugin guards: the actions it can refuse or change (before hooks) or shape (filters).
            // Kept in its status, so the actions stay locked if this plugin is enforcing and a later start has to disable it.
            static string Name(Type action) => action.GetCustomAttribute<Kb.Plugins.Actions.ActionAttribute>()?.Name ?? action.FullName!;
            var guarded = registrations.All<Kb.Plugins.Actions.BeforeHook>().Select(r => (r.PluginId, Action: Name(r.Value.Action)))
                .Concat(registrations.All<Kb.Plugins.Actions.ResultFilter>().Select(r => (r.PluginId, Action: Name(r.Value.Action)))).ToLookup(g => g.PluginId, g => g.Action);
            foreach (var c in enabled.Where(c => loaded.ContainsKey(c.Manifest.Id) && c.Status is not null))
            {
                var guards = PluginGuards.Write(guarded[c.Manifest.Id]);
                if (c.Status!.GuardsJson != guards) c.Status.GuardsJson = guards;
            }
        }
        if (events.Count > 0)
        {
            // The core turned something off here. Another server may still run it, so the generation is raised: that server restarts.
            // This one already runs the new set, so it keeps the raised number and does not restart itself.
            generation++;
            var text = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Key == RuntimeSettingKeys.PluginGeneration, ct) is { } row) (row.Value, row.UpdatedAt) = (text, now);
            else db.RuntimeSettings.Add(new RuntimeSetting { Key = RuntimeSettingKeys.PluginGeneration, Value = text, UpdatedAt = now });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException gone)
        {
            // A row this start wanted to change was removed by another server meanwhile. Its change stands; the rest is saved.
            foreach (var entry in gone.Entries) entry.State = EntityState.Detached;
            await db.SaveChangesAsync(ct);
        }
        if (!safeMode)
        {
            // A plugin this server did not load keeps no files here. Best effort: a folder in use is tried again at the next start.
            foreach (var folder in Directory.GetDirectories(work).Where(d => !loaded.ContainsKey(Path.GetFileName(d))))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        // ---- what the app gets
        foreach (var id in loaded.Keys)
        {
            // Each loaded plugin has a named HTTP client that refuses any host its manifest does not declare. The guard sees each
            // request the plugin makes; a redirect would be followed below it, to any host, so redirects are not followed.
            // The plugin gets the 3xx answer and may ask again: that request is checked like any other.
            var hosts = byId[id].Manifest.OutboundHosts.ToArray();
            services.AddHttpClient(PluginHttp.ClientName(id))
                .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false })
                .AddHttpMessageHandler(() => new OutboundHostGuard(hosts));
        }
        var registry = new PluginRegistry(
            [.. candidates.Select(c => new PluginInfo(c.Manifest, c.Origin, c.Status?.State ?? PluginState.Enabled, c.Status?.Reason, c.Status?.LockedAt is not null, loaded.ContainsKey(c.Manifest.Id), c.Source)
                { Guards = PluginGuards.Read(c.Status?.GuardsJson) })],
            safeMode, generation, loaded, folders.Where(f => loaded.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
        services.AddSingleton(registry);
        services.AddSingleton<IRegistrations>(registrations);
        services.AddSingleton(settings);
        return new PluginBoot(registry, registrations, events) { PluginServices = pluginServices };
    }

    /// <summary>
    /// Call last, just before the host is built. Puts the plugins' services in front of every other registration,
    /// so a core or framework registration of the same service wins, also one made with TryAdd. A plugin adds to the app; it does not replace a core service.
    /// </summary>
    public static void Seal(IServiceCollection services, PluginBoot boot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(boot);
        for (var i = 0; i < boot.PluginServices.Count; i++) services.Insert(i, boot.PluginServices[i]);
    }

    /// <summary>The work folder, or a folder under the temp folder when the app runs from a place it cannot write to.</summary>
    private static string WorkFolder(string configured, string contentRoot)
    {
        var folder = Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kb-plugins")).FullName;
        }
    }

    /// <summary>The folder of this version, unpacked. It is used as it is when its marker names this package; folders of other versions go.</summary>
    private static string Unpacked(string work, PluginManifest manifest, string sha256, byte[] package)
    {
        var root = Path.Combine(work, manifest.Id);
        var folder = Path.Combine(root, manifest.Version);
        var marker = Path.Combine(folder, ".sha256");
        if (!File.Exists(marker) || File.ReadAllText(marker) != sha256)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            PluginPackageReader.Unpack(package, folder);
            File.WriteAllText(marker, sha256);
        }
        foreach (var other in Directory.GetDirectories(root).Where(d => !string.Equals(Path.GetFullPath(d), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)))
        {
            try { Directory.Delete(other, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // in use; the next start tries again
        }
        return folder;
    }
}
