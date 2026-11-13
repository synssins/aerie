using System.Globalization;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Plugins;

public sealed record PluginSource(string Kind, string? Url = null)
{
    public static readonly PluginSource Upload = new("upload");
}

public sealed record InstalledDto(string Id, string Version, bool Restart);

/// <summary>A plugin as it is in the database now, which can be ahead of what this server loaded at its start.</summary>
public sealed record KnownPlugin(PluginManifest Manifest, PluginStatus Status, PluginOrigin Origin, string Source, string Sha256);

/// <summary>
/// The one path for every plugin change: install, update, enable, disable, remove, and the core's own disable.
/// Every change writes its rows, raises the plugin generation, writes an audit entry with the package hash and source, then asks for a restart.
/// A marketplace later calls <see cref="InstallAsync"/> with the bytes it fetched.
/// </summary>
public sealed class PluginInstaller(KbDbContext db, PluginRegistry registry, PluginWatcher watcher, IAppRestarter restarter, IAuditLog audit, TimeProvider time,
    IEnumerable<IFileStoreKind> storeKinds, IEnumerable<ILogShipperKind> logKinds)
{
    public async Task<List<KnownPlugin>> KnownAsync(CancellationToken ct)
    {
        var statuses = await db.PluginStatuses.Where(s => !s.Removed).ToListAsync(ct);
        var packages = await db.PluginPackages.AsNoTracking().Select(p => new { p.PluginId, p.Version, p.ManifestJson, p.Source, p.Sha256 }).ToListAsync(ct);
        var known = new List<KnownPlugin>();
        foreach (var status in statuses.OrderBy(s => s.PluginId, StringComparer.Ordinal))
        {
            if (status.ActiveVersion is null)
            {
                if (registry.Find(status.PluginId) is { Origin: PluginOrigin.BuiltIn } builtIn) known.Add(new(builtIn.Manifest, status, PluginOrigin.BuiltIn, "built-in", ""));
            }
            else if (packages.FirstOrDefault(p => p.PluginId == status.PluginId && p.Version == status.ActiveVersion) is { } package && PluginManifests.Parse(package.ManifestJson) is { } manifest)
                known.Add(new(manifest, status, PluginOrigin.Uploaded, package.Source, package.Sha256));
        }
        return known;
    }

    public async Task<Outcome<InstalledDto>> InstallAsync(byte[] package, PluginSource source, Guid? by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var read = PluginPackageReader.Read(package);
        if (!read.Ok) return read.Problem!;
        var manifest = read.Value!.Manifest;
        if (registry.Find(manifest.Id) is { Origin: PluginOrigin.BuiltIn }) return Problem.Conflict("Built-in plugins cannot be replaced.");
        if (PluginVersions.Refusal(manifest) is { } refusal) return Problem.Conflict(refusal);

        var known = await KnownAsync(ct);
        var status = await db.PluginStatuses.FirstOrDefaultAsync(s => s.PluginId == manifest.Id, ct);
        var update = status is { ActiveVersion: not null, Removed: false };
        if (update && status!.ActiveVersion == manifest.Version) return Problem.Conflict($"Version {manifest.Version} is already installed.");
        if (update && !PluginVersions.AtLeast(manifest.Version, status!.ActiveVersion!)) return Problem.Conflict("A newer version is installed.");
        var others = known.Where(k => k.Manifest.Id != manifest.Id).Select(k => k.Manifest).ToList();
        if (PluginGraph.Missing(manifest, others) is [var missing, ..])
            return Problem.Conflict($"Requires {NameOf(missing.Id, known)} {missing.MinVersion} or later.");
        if (others.FirstOrDefault(o => PluginId.TablePrefix(o.Id) == PluginId.TablePrefix(manifest.Id)) is { } clash) return Problem.Conflict($"Conflicts with {clash.Id}.");

        var now = time.GetUtcNow();
        // An update deletes the older package. To go back, remove the plugin and upload the older package.
        await db.PluginPackages.Where(p => p.PluginId == manifest.Id).ExecuteDeleteAsync(ct);
        db.PluginPackages.Add(new PluginPackage
        {
            PluginId = manifest.Id, Version = manifest.Version, Bytes = package, Sha256 = read.Value.Sha256, ManifestJson = PluginManifests.Write(manifest),
            Source = source.Url ?? source.Kind, InstalledBy = by, InstalledAt = now,
        });
        if (status is null) db.PluginStatuses.Add(new PluginStatus { PluginId = manifest.Id, State = PluginState.Enabled, ActiveVersion = manifest.Version, UpdatedAt = now });
        else
        {
            // An update keeps the state it had.
            if (status.Removed) (status.Removed, status.State, status.Reason, status.LockedAt) = (false, PluginState.Enabled, null, null);
            (status.ActiveVersion, status.UpdatedAt) = (manifest.Version, now);
        }
        await db.SaveChangesAsync(ct);
        await ChangedAsync(update ? AuditActions.PluginUpdate : AuditActions.PluginInstall, manifest.Id, by, read.Value.Sha256, source.Url ?? source.Kind, manifest.Version, null, ct);
        return new InstalledDto(manifest.Id, manifest.Version, Restart: true);
    }

    public async Task<Outcome<Unit>> SetEnabledAsync(string id, bool enabled, Guid by, CancellationToken ct)
    {
        var known = await KnownAsync(ct);
        if (known.FirstOrDefault(k => k.Manifest.Id == id) is not { } plugin) return Problem.NotFound;
        if ((plugin.Status.State == PluginState.Enabled) == enabled) return default(Unit);
        if (enabled)
        {
            foreach (var r in plugin.Manifest.Requires)
            {
                var required = known.FirstOrDefault(k => k.Manifest.Id == r.Id);
                if (required is null || required.Status.State != PluginState.Enabled || !PluginVersions.AtLeast(required.Manifest.Version, r.MinVersion))
                    return Problem.Conflict($"Requires {NameOf(r.Id, known)}.");
            }
        }
        else if (await HeldAsync(plugin, known, ct) is { } held) return held;

        // A plugin an Admin turns off or on carries no reason and no lock.
        (plugin.Status.State, plugin.Status.Reason, plugin.Status.LockedAt, plugin.Status.UpdatedAt) = (enabled ? PluginState.Enabled : PluginState.Disabled, null, null, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await ChangedAsync(enabled ? AuditActions.PluginEnable : AuditActions.PluginDisable, id, by, plugin.Sha256, plugin.Source, plugin.Manifest.Version, null, ct);
        return default(Unit);
    }

    /// <summary>Deletes the packages and the settings. Until plugins have tables of their own there is no data to keep, so the status row goes too.</summary>
    public async Task<Outcome<Unit>> RemoveAsync(string id, bool deleteData, Guid by, CancellationToken ct)
    {
        _ = deleteData;
        var known = await KnownAsync(ct);
        if (known.FirstOrDefault(k => k.Manifest.Id == id) is not { } plugin) return Problem.NotFound;
        if (plugin.Origin == PluginOrigin.BuiltIn) return Problem.Conflict("Built-in plugins cannot be removed.");
        if (await HeldAsync(plugin, known, ct) is { } held) return held;

        await db.PluginPackages.Where(p => p.PluginId == id).ExecuteDeleteAsync(ct);
        await db.PluginSettingValues.Where(v => v.PluginId == id).ExecuteDeleteAsync(ct);
        db.PluginStatuses.Remove(plugin.Status);
        await db.SaveChangesAsync(ct);
        await ChangedAsync(AuditActions.PluginRemove, id, by, plugin.Sha256, plugin.Source, plugin.Manifest.Version, null, ct);
        return default(Unit);
    }

    /// <summary>Deletes what a removed plugin left behind.</summary>
    public async Task<Outcome<Unit>> DeleteDataAsync(string id, Guid by, CancellationToken ct)
    {
        var status = await db.PluginStatuses.FirstOrDefaultAsync(s => s.PluginId == id, ct);
        if (status is null) return Problem.NotFound;
        if (!status.Removed) return Problem.Conflict("Remove the plugin first.");
        db.PluginStatuses.Remove(status);
        await db.SaveChangesAsync(ct);
        await ChangedAsync(AuditActions.PluginDataDelete, id, by, "", "", status.ActiveVersion ?? "", null, ct);
        return default(Unit);
    }

    public async Task<Outcome<Unit>> ReleaseLockAsync(string id, Guid by, CancellationToken ct)
    {
        var known = await KnownAsync(ct);
        if (known.FirstOrDefault(k => k.Manifest.Id == id) is not { } plugin) return Problem.NotFound;
        (plugin.Status.LockedAt, plugin.Status.UpdatedAt) = (null, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await ChangedAsync(AuditActions.PluginLockRelease, id, by, plugin.Sha256, plugin.Source, plugin.Manifest.Version, null, ct);
        return default(Unit);
    }

    /// <summary>
    /// The core turns a broken plugin off: the reason is kept, an enforcing plugin is locked, and the plugins that require it go with it.
    /// It does not check what holds the plugin: a broken plugin is off whatever depends on it.
    /// </summary>
    public async Task DisableByCoreAsync(string id, string reason, CancellationToken ct)
    {
        var known = await KnownAsync(ct);
        if (known.FirstOrDefault(k => k.Manifest.Id == id) is not { Status.State: PluginState.Enabled } plugin) return;
        var now = time.GetUtcNow();
        reason = reason.Split('\r', '\n')[0];
        reason = reason[..Math.Min(reason.Length, 300)];
        var enabled = known.Where(k => k.Status.State == PluginState.Enabled).ToList();
        var dependents = PluginGraph.Dependents(id, [.. enabled.Select(k => k.Manifest)]).Select(d => enabled.First(k => k.Manifest.Id == d)).ToList();

        (plugin.Status.State, plugin.Status.Reason, plugin.Status.UpdatedAt) = (PluginState.DisabledByCore, reason, now);
        if (plugin.Manifest.Enforcing) plugin.Status.LockedAt = now;
        foreach (var d in dependents) (d.Status.State, d.Status.Reason, d.Status.UpdatedAt) = (PluginState.DisabledByCore, $"Requires {PluginManifests.NameOf(plugin.Manifest)}.", now);
        await db.SaveChangesAsync(ct);
        foreach (var d in dependents)
            await audit.AppendAsync(new AuditEvent(AuditActions.PluginDisabledByCore, null, "plugin", d.Manifest.Id, Data: Data(d.Sha256, d.Source, d.Manifest.Version, d.Status.Reason)), ct);
        await ChangedAsync(AuditActions.PluginDisabledByCore, id, null, plugin.Sha256, plugin.Source, plugin.Manifest.Version, reason, ct);
    }

    /// <summary>Why a plugin cannot be turned off or removed now, or null: enabled plugins require it, or a storage place or log target uses a kind only it supplies.</summary>
    private async Task<Problem?> HeldAsync(KnownPlugin plugin, List<KnownPlugin> known, CancellationToken ct)
    {
        var enabled = known.Where(k => k.Status.State == PluginState.Enabled).ToList();
        var dependents = PluginGraph.Dependents(plugin.Manifest.Id, [.. enabled.Select(k => k.Manifest)]);
        if (dependents.Count > 0) return Problem.Conflict($"Required by {string.Join(", ", dependents.Select(d => NameOf(d, known)))}.");

        if (registry.AssemblyOf(plugin.Manifest.Id) is not { } assembly) return null;
        var stores = storeKinds.Where(k => k.GetType().Assembly == assembly && !storeKinds.Any(o => o.Kind == k.Kind && o.GetType().Assembly != assembly)).Select(k => k.Kind).ToList();
        if (stores.Count > 0 && await db.StoragePlaces.AnyAsync(p => p.Role != StorageRole.Old && stores.Contains(p.Kind), ct)) return Problem.Conflict("In use for file storage.");
        var logs = logKinds.Where(k => k.GetType().Assembly == assembly && !logKinds.Any(o => o.Kind == k.Kind && o.GetType().Assembly != assembly)).Select(k => k.Kind).ToList();
        if (logs.Count > 0 && await db.LogTargets.AnyAsync(t => t.Enabled && logs.Contains(t.Kind), ct)) return Problem.Conflict("In use for log shipping.");
        return null;
    }

    private static string NameOf(string id, List<KnownPlugin> known) =>
        known.FirstOrDefault(k => k.Manifest.Id == id) is { } plugin ? PluginManifests.NameOf(plugin.Manifest) : id;

    private static Dictionary<string, string> Data(string sha256, string source, string version, string? reason)
    {
        var data = new Dictionary<string, string> { ["sha256"] = sha256, ["source"] = source, ["version"] = version };
        if (reason is not null) data["reason"] = reason;
        return data;
    }

    private async Task ChangedAsync(string action, string id, Guid? by, string sha256, string source, string version, string? reason, CancellationToken ct)
    {
        var generation = (long.TryParse(await db.GetAsync(RuntimeSettingKeys.PluginGeneration, ct), out var g) ? g : 0) + 1;
        await db.SetAsync(RuntimeSettingKeys.PluginGeneration, generation.ToString(CultureInfo.InvariantCulture), by, time.GetUtcNow(), ct);
        watcher.Saw(generation);
        await audit.AppendAsync(new AuditEvent(action, by, "plugin", id, Data: Data(sha256, source, version, reason)), ct);
        restarter.Restart($"{action} {id}");
    }
}
