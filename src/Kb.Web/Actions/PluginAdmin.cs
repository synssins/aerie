using System.Text.Json.Serialization;
using Kb.Core.Plugins;
using Kb.Data;
using Kb.Web.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Administration > Plugins. These actions are core only: they are not in the contract assembly, so no plugin can hook them
// (a plugin must not be able to refuse its own removal).
// Every action here is an Admin's: AuthorizeAsync answers 403 to anyone else. An Admin is always a signed-in person, so ctx.Actor.UserId is set in HandleAsync.
// PluginInstaller and PluginSettingsStore write their own audit entries, with the caller as actor: no handler adds one.

public sealed record PluginInfoDto(string Id, string Name, string Version, string Publisher, string? Description, PluginOrigin Origin, PluginState State, string? Reason,
    bool Locked, bool Loaded, string Source, IReadOnlyList<string> Requires, IReadOnlyList<string> RequiredBy, IReadOnlyList<string> ExtensionPoints, IReadOnlyList<string> OutboundHosts,
    IReadOnlyList<PluginPermission> Permissions, bool Enforcing, IReadOnlyList<PluginSetting> Settings, IReadOnlyList<string> Replaces, bool Removed);
public sealed record PluginListDto(IReadOnlyList<PluginInfoDto> Plugins, bool SafeMode);
/// <summary>Restart: a setting marked "restart" changed, so the app restarts.</summary>
public sealed record PluginSettingsSaved(bool Restart);

[Action("plugin.list", Read = true)]
public sealed record ListPlugins : IAction<PluginListDto>;

[Action("plugin.install")]
public sealed record InstallPlugin : IAction<InstalledDto>
{
    [JsonIgnore] public required UploadContent Package { get; init; }
}

[Action("plugin.state.set")]
public sealed record SetPluginState : IAction<Unit>
{
    public required string Id { get; init; }
    public required bool Enabled { get; init; }
}

[Action("plugin.remove")]
public sealed record RemovePlugin : IAction<Unit>
{
    public required string Id { get; init; }
    public bool DeleteData { get; init; }
}

[Action("plugin.data.delete")]
public sealed record DeletePluginData : IAction<Unit>
{
    public required string Id { get; init; }
}

[Action("plugin.lock.release")]
public sealed record ReleasePluginLock : IAction<Unit>
{
    public required string Id { get; init; }
}

[Action("plugin.settings.read", Read = true)]
public sealed record ReadPluginSettings : IAction<PluginSettingsDto>
{
    public required string Id { get; init; }
}

[Action("plugin.settings.save")]
public sealed record SavePluginSettings : IAction<PluginSettingsSaved>
{
    public required string Id { get; init; }
    /// <summary>A value can be a secret.</summary>
    [JsonIgnore] public required IReadOnlyDictionary<string, string?> Values { get; init; }
}

internal sealed class ListPluginsHandler(PluginInstaller installer, PluginRegistry registry, IAccess access) : IActionHandler<ListPlugins, PluginListDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListPlugins> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    // The list is what the database says now; "loaded" is what this server loaded when it started.
    public async Task<Outcome<PluginListDto>> HandleAsync(ActionContext<ListPlugins> ctx)
    {
        var known = await installer.KnownAsync(ctx.Cancel);
        var manifests = known.Select(k => k.Manifest).ToList();
        return new PluginListDto([.. known.Select(k => new PluginInfoDto(
            k.Manifest.Id, PluginManifests.NameOf(k.Manifest), k.Manifest.Version, k.Manifest.Publisher, k.Manifest.Description, k.Origin, k.Status.State, k.Status.Reason,
            k.Status.LockedAt is not null, registry.Find(k.Manifest.Id) is { Loaded: true } loaded && loaded.Manifest.Version == k.Manifest.Version, k.Source,
            [.. k.Manifest.Requires.Select(r => r.Id)], PluginGraph.Dependents(k.Manifest.Id, manifests), k.Manifest.ExtensionPoints, k.Manifest.OutboundHosts,
            k.Manifest.Permissions, k.Manifest.Enforcing, k.Manifest.Settings, k.Manifest.Ui?.Replaces ?? [], Removed: false)),
            // Removed plugins whose tables were kept: listed so their data can still be deleted.
            .. (await installer.RemovedAsync(ctx.Cancel)).Select(s => new PluginInfoDto(s.PluginId, s.PluginId, s.ActiveVersion ?? "", "", null, PluginOrigin.Uploaded, PluginState.Disabled, null,
                Locked: false, Loaded: false, "upload", [], [], [], [], [], Enforcing: false, [], [], Removed: true))], registry.SafeMode);
    }
}

internal sealed class InstallPluginHandler(PluginInstaller installer, IAccess access) : IActionHandler<InstallPlugin, InstalledDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<InstallPlugin> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    // The package is the raw request body. The web server's own size limit still applies and answers 413.
    public async Task<Outcome<InstalledDto>> HandleAsync(ActionContext<InstallPlugin> ctx)
    {
        if (ctx.Action.Package is PluginUpload upload) upload.Allow(PluginPackageReader.MaxUnpackedBytes);
        // shortcut: the stream is not disposed, as before (it is the request's body); dispose it once a package can come from somewhere else.
        var stream = await ctx.Action.Package.OpenReadAsync(ctx.Cancel);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ctx.Cancel)) > 0)
        {
            if (body.Length + n > PluginPackageReader.MaxUnpackedBytes) return new Problem(400, "Package too large.");
            body.Write(buffer, 0, n);
        }
        return await installer.InstallAsync(body.ToArray(), PluginSource.Upload, ctx.Actor.UserId, ctx.Cancel);
    }
}

internal sealed class SetPluginStateHandler(PluginInstaller installer, IAccess access) : IActionHandler<SetPluginState, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SetPluginState> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public Task<Outcome<Unit>> HandleAsync(ActionContext<SetPluginState> ctx) => installer.SetEnabledAsync(ctx.Action.Id, ctx.Action.Enabled, ctx.Actor.UserId!.Value, ctx.Cancel);
}

internal sealed class RemovePluginHandler(PluginInstaller installer, IAccess access) : IActionHandler<RemovePlugin, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<RemovePlugin> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public Task<Outcome<Unit>> HandleAsync(ActionContext<RemovePlugin> ctx) => installer.RemoveAsync(ctx.Action.Id, ctx.Action.DeleteData, ctx.Actor.UserId!.Value, ctx.Cancel);
}

internal sealed class DeletePluginDataHandler(PluginInstaller installer, IAccess access) : IActionHandler<DeletePluginData, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<DeletePluginData> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public Task<Outcome<Unit>> HandleAsync(ActionContext<DeletePluginData> ctx) => installer.DeleteDataAsync(ctx.Action.Id, ctx.Actor.UserId!.Value, ctx.Cancel);
}

internal sealed class ReleasePluginLockHandler(PluginInstaller installer, IAccess access) : IActionHandler<ReleasePluginLock, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReleasePluginLock> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public Task<Outcome<Unit>> HandleAsync(ActionContext<ReleasePluginLock> ctx) => installer.ReleaseLockAsync(ctx.Action.Id, ctx.Actor.UserId!.Value, ctx.Cancel);
}

/// <summary>A secret's value is never returned: only which secrets are set.</summary>
internal sealed class ReadPluginSettingsHandler(PluginRegistry registry, KbDbContext db, IAccess access) : IActionHandler<ReadPluginSettings, PluginSettingsDto>
{
    private PluginInfo? _plugin;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadPluginSettings> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_plugin = registry.Find(ctx.Action.Id)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<PluginSettingsDto>> HandleAsync(ActionContext<ReadPluginSettings> ctx)
    {
        var (id, plugin) = (ctx.Action.Id, _plugin!);
        var rows = await db.PluginSettingValues.AsNoTracking().Where(v => v.PluginId == id).ToListAsync(ctx.Cancel);
        // What the manifest calls a secret is one, also when its row was stored in clear by an older version of the plugin.
        bool Secret(PluginSettingValue row) => row.Secret || plugin.Manifest.Settings.Any(d => d.Key == row.Key && PluginSettingsStore.IsSecret(d));
        return new PluginSettingsDto(rows.Where(r => !Secret(r)).ToDictionary(r => r.Key, r => r.Value), [.. rows.Where(Secret).Select(r => r.Key)]);
    }
}

internal sealed class SavePluginSettingsHandler(PluginSettingsStore store, IAccess access) : IActionHandler<SavePluginSettings, PluginSettingsSaved>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SavePluginSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<PluginSettingsSaved>> HandleAsync(ActionContext<SavePluginSettings> ctx)
    {
        var saved = await store.SaveAsync(ctx.Action.Id, ctx.Action.Values, ctx.Actor.UserId!.Value, ctx.Cancel);
        return saved.Problem is { } problem ? problem : new PluginSettingsSaved(saved.Value);
    }
}
