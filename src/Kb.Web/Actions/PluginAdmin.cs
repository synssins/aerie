using Kb.Core.Spaces;
using Kb.Core.Audit;
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
    IReadOnlyList<PluginPermission> Permissions, bool Enforcing, IReadOnlyList<PluginSetting> Settings, IReadOnlyList<string> Replaces, bool Removed,
    IReadOnlyList<string> ImportFileKinds, IReadOnlyList<PluginSignInMethodDto> SignInMethods);
/// <summary>A sign-in method a plugin adds: its id ("{plugin}/{id}"), its button text, and the redirect address to register with the provider.</summary>
public sealed record PluginSignInMethodDto(string Id, string Name, string RedirectAddress);
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

internal sealed class ListPluginsHandler(PluginInstaller installer, PluginRegistry registry, IRegistrations registrations, IAccess access, PluginSettingsStore settings,
    SignInMethods signInMethods, IHttpContextAccessor http) : IActionHandler<ListPlugins, PluginListDto>
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
            // Its settings: the manifest's, and the app's for each sign-in method it adds.
            k.Manifest.Permissions, k.Manifest.Enforcing, settings.Definitions(k.Manifest), k.Manifest.Ui?.Replaces ?? [], Removed: false,
            // The file kinds it reads for import: what this server loaded, so none for a plugin that is not loaded.
            [.. registrations.All<ImportFileKindReg>().Where(r => r.PluginId == k.Manifest.Id).SelectMany(r => r.Value.Extensions).Distinct().Order(StringComparer.Ordinal)],
            // Its sign-in methods: likewise what this server loaded. The redirect address is this site as the Admin reached it.
            [.. signInMethods.All.Where(m => m.PluginId == k.Manifest.Id).Select(m => new PluginSignInMethodDto(m.Key, m.Name, SignInMethods.RedirectAddress(http.HttpContext!.Request, m)))])),
            // Removed plugins whose tables were kept: listed so their data can still be deleted.
            .. (await installer.RemovedAsync(ctx.Cancel)).Select(s => new PluginInfoDto(s.PluginId, s.PluginId, s.ActiveVersion ?? "", "", null, PluginOrigin.Uploaded, PluginState.Disabled, null,
                Locked: false, Loaded: false, "upload", [], [], [], [], [], Enforcing: false, [], [], Removed: true, [], []))], registry.SafeMode);
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
internal sealed class ReadPluginSettingsHandler(PluginRegistry registry, KbDbContext db, IAccess access, PluginSettingsStore store) : IActionHandler<ReadPluginSettings, PluginSettingsDto>
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
        var definitions = store.Definitions(plugin.Manifest);
        bool Secret(PluginSettingValue row) => row.Secret || definitions.Any(d => d.Key == row.Key && PluginSettingsStore.IsSecret(d));
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

/// <summary>Status: Healthy, Degraded or Unhealthy. Text: what the check said, or the error it threw.</summary>
public sealed record PluginCheckDto(string Name, string Status, string? Text);
public sealed record PluginJobDto(string Name, int EveryMinutes, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, string? LastError);
public sealed record PluginHealthEntry(string Id, IReadOnlyList<PluginCheckDto> Checks, IReadOnlyList<PluginJobDto> Jobs);
public sealed record PluginHealthDto(IReadOnlyList<PluginHealthEntry> Plugins);

/// <summary>The health checks and jobs of the loaded plugins that have any. The checks run when this is asked for.</summary>
[Action("plugin.health", Read = true)]
public sealed record ReadPluginHealth : IAction<PluginHealthDto>;

internal sealed class ReadPluginHealthHandler(KbDbContext db, IAccess access, IRegistrations registrations, PluginRegistry registry, IServiceScopeFactory scopes)
    : IActionHandler<ReadPluginHealth, PluginHealthDto>
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadPluginHealth> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<PluginHealthDto>> HandleAsync(ActionContext<ReadPluginHealth> ctx)
    {
        var runs = await db.JobRuns.AsNoTracking().ToListAsync(ctx.Cancel);
        var plugins = new List<PluginHealthEntry>();
        foreach (var plugin in registry.Loaded)
        {
            var id = plugin.Manifest.Id;
            var checks = new List<PluginCheckDto>();
            foreach (var check in registrations.All<HealthReg>().Where(r => r.PluginId == id)) checks.Add(await RunAsync(id, check.Value, ctx.Cancel));
            var jobs = plugin.Manifest.Schedules.Select(s => (Schedule: s, Run: runs.FirstOrDefault(r => r.PluginId == id && r.Name == s.Name)))
                .Select(j => new PluginJobDto(j.Schedule.Name, j.Schedule.EveryMinutes, j.Run?.LastRunAt, j.Run?.NextRunAt, j.Run?.LastError)).ToList();
            if (checks.Count > 0 || jobs.Count > 0) plugins.Add(new PluginHealthEntry(id, checks, jobs));
        }
        return new PluginHealthDto(plugins);
    }

    /// <summary>
    /// A check that throws or takes too long is unhealthy. It is not counted as a failure of the plugin: an Admin looking must not turn it off.
    /// It runs in its own scope as its plugin, like a job: what it does through the bus is done as the plugin, not as the Admin who opened the screen.
    /// </summary>
    private async Task<PluginCheckDto> RunAsync(string pluginId, HealthReg reg, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<Kb.Web.Pipeline.ActorAccessor>().Use(Actor.Plugin(pluginId, "health"));
            var check = (Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck)ActivatorUtilities.CreateInstance(scope.ServiceProvider, reg.Check);
            var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext
            {
                Registration = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(reg.Name, check, null, null),
            };
            // The limit holds also for a check that ignores its cancellation token.
            var result = await check.CheckHealthAsync(context, timeout.Token).WaitAsync(CheckTimeout, ct);
            return new PluginCheckDto(reg.Name, result.Status.ToString(), result.Description);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            var text = e is OperationCanceledException or TimeoutException ? "Timed out." : e.Message.Split('\r', '\n')[0];
            return new PluginCheckDto(reg.Name, "Unhealthy", text[..Math.Min(text.Length, 300)]);
        }
    }
}

/// <summary>Role: the least role that holds a space permission now; null for a global permission, which is given to people one by one.</summary>
public sealed record PluginPermissionDto(string Key, string Label, string Scope, string DefaultRole, string? Role);
public sealed record PluginPermissionsDto(IReadOnlyList<PluginPermissionDto> Permissions);

[Action("plugin.permissions.read", Read = true)]
public sealed record ReadPluginPermissions : IAction<PluginPermissionsDto>
{
    public required string Id { get; init; }
}

/// <summary>Sets the least role that holds each named space permission of a plugin.</summary>
[Action("plugin.permissions.save")]
public sealed record SavePluginPermissions : IAction<Unit>
{
    public required string Id { get; init; }
    public required IReadOnlyDictionary<string, string> Roles { get; init; }
}

internal sealed class ReadPluginPermissionsHandler(KbDbContext db, IAccess access, PluginRegistry registry) : IActionHandler<ReadPluginPermissions, PluginPermissionsDto>
{
    private PluginInfo? _plugin;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadPluginPermissions> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_plugin = registry.Find(ctx.Action.Id)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<PluginPermissionsDto>> HandleAsync(ActionContext<ReadPluginPermissions> ctx)
    {
        var id = _plugin!.Manifest.Id;
        var set = await db.PluginPermissionRoles.AsNoTracking().Where(p => p.Permission.StartsWith(id + ":")).ToDictionaryAsync(p => p.Permission, p => p.MinRole, ctx.Cancel);
        return new PluginPermissionsDto([.. _plugin.Manifest.Permissions.Select(p => new PluginPermissionDto(p.Key, p.Label, p.Scope, PluginPermissionScopes.DefaultRole(p).ToString(),
            p.Scope == PluginPermissionScopes.Space ? (set.TryGetValue($"{id}:{p.Key}", out var role) ? role : PluginPermissionScopes.DefaultRole(p)).ToString() : null))]);
    }
}

internal sealed class SavePluginPermissionsHandler(KbDbContext db, IAccess access, PluginRegistry registry) : IActionHandler<SavePluginPermissions, Unit>
{
    private PluginInfo? _plugin;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SavePluginPermissions> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_plugin = registry.Find(ctx.Action.Id)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SavePluginPermissions> ctx)
    {
        var (id, roles) = (_plugin!.Manifest.Id, new Dictionary<string, SpaceRole>(StringComparer.Ordinal));
        foreach (var (key, text) in ctx.Action.Roles)
        {
            if (_plugin.Manifest.Permissions.FirstOrDefault(p => p.Key == key) is not { Scope: PluginPermissionScopes.Space }) return Problem.Invalid(key, "Not a space permission of this plugin.");
            if (!Enum.TryParse<SpaceRole>(text, out var role) || !Enum.IsDefined(role)) return Problem.Invalid(key, "Reader, Contributor, Supervisor or Owner.");
            roles[key] = role;
        }
        var names = roles.Keys.Select(k => $"{id}:{k}").ToList();
        var rows = await db.PluginPermissionRoles.Where(p => names.Contains(p.Permission)).ToListAsync(ctx.Cancel);
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, role) in roles)
        {
            // A role equal to the manifest's default is not stored: the default of a later version of the plugin then counts.
            var standard = PluginPermissionScopes.DefaultRole(_plugin.Manifest.Permissions.First(p => p.Key == key));
            var row = rows.FirstOrDefault(r => r.Permission == $"{id}:{key}");
            if ((row?.MinRole ?? standard) == role && (row is null || role != standard)) continue;
            if (role == standard) db.PluginPermissionRoles.Remove(row!);
            else if (row is not null) row.MinRole = role;
            else db.PluginPermissionRoles.Add(new PluginPermissionRole { Permission = $"{id}:{key}", MinRole = role });
            changed[key] = role.ToString();
        }
        if (changed.Count == 0) return new Unit();
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.PluginPermissionsChange, "plugin", id, data: changed);
        return new Unit();
    }
}
