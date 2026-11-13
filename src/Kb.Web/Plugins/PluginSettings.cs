using System.Globalization;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Plugins;

/// <summary>
/// Plugin settings set in the app. One per app: values that are not secret are answered from memory (the snapshot a plugin's context reads),
/// so a change applies without a restart; a secret is protected in the database and read only when a plugin asks for it.
/// </summary>
public sealed class PluginSettingsStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly PluginRegistry _registry;
    private readonly PluginSettingsSnapshot _snapshot;
    private readonly IDataProtectionProvider _protection;
    private readonly PluginWatcher _watcher;
    private readonly IAppRestarter _restarter;
    private readonly TimeProvider _time;

    public PluginSettingsStore(IServiceScopeFactory scopes, PluginRegistry registry, PluginSettingsSnapshot snapshot, IDataProtectionProvider protection,
        PluginWatcher watcher, IAppRestarter restarter, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        (_scopes, _registry, _snapshot, _protection, _watcher, _restarter, _time) = (scopes, registry, snapshot, protection, watcher, restarter, time);
        // From here on a plugin's context can read its secrets.
        snapshot.SecretReader = GetSecretAsync;
    }

    public static bool IsSecret(PluginSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.Secret || setting.Type == "secret";
    }

    private IDataProtector Protector(string pluginId) => _protection.CreateProtector("kb.plugin.secret:" + pluginId);

    public string? Get(string pluginId, string key) => _snapshot.Get(pluginId, key);

    public async Task<string?> GetSecretAsync(string pluginId, string key, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<KbDbContext>().PluginSettingValues.AsNoTracking().FirstOrDefaultAsync(v => v.PluginId == pluginId && v.Key == key, ct);
        // A value stored in clear before an update made the setting a secret is still the plugin's secret.
        return row?.Value is null ? null : row.Secret ? Protector(pluginId).Unprotect(row.Value) : row.Value;
    }

    /// <summary>
    /// Saves the values given. A value left out or null is kept; an empty one is cleared. Nothing is saved when any value is refused.
    /// True: a setting marked "restart" changed, so the app restarts.
    /// </summary>
    public async Task<Outcome<bool>> SaveAsync(string pluginId, IReadOnlyDictionary<string, string?> values, Guid by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_registry.Find(pluginId) is not { } plugin) return Problem.NotFound;
        var definitions = plugin.Manifest.Settings;
        if (values.Keys.FirstOrDefault(k => definitions.All(d => d.Key != k)) is { } unknown) return Problem.Invalid(unknown, "Unknown setting.");

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var rows = await db.PluginSettingValues.Where(v => v.PluginId == pluginId).ToListAsync(ct);
        var now = _time.GetUtcNow();
        var changed = new List<string>();
        var restart = false;
        foreach (var d in definitions)
        {
            var row = rows.FirstOrDefault(r => r.Key == d.Key);
            var given = values.GetValueOrDefault(d.Key);
            if (given is { Length: > 0 } && !IsSecret(d) && Refusal(d, given) is { } refusal) return Problem.Invalid(d.Key, refusal);
            if (d.Required && string.IsNullOrEmpty(given ?? row?.Value)) return Problem.Invalid(d.Key, "Required.");
            if (given is null) continue;

            var before = row?.Value is null ? null : row.Secret ? Protector(pluginId).Unprotect(row.Value) : row.Value;
            // The same value again is still a change when it is stored in clear and the setting is a secret now.
            if ((before ?? "") == given && (row is null || row.Secret == IsSecret(d))) continue;
            if (given.Length == 0) db.PluginSettingValues.Remove(row!);
            else
            {
                if (row is null) db.PluginSettingValues.Add(row = new PluginSettingValue { PluginId = pluginId, Key = d.Key });
                (row.Value, row.Secret, row.UpdatedAt, row.UpdatedBy) = (IsSecret(d) ? Protector(pluginId).Protect(given) : given, IsSecret(d), now, by);
            }
            changed.Add(d.Key);
            restart |= d.Restart;
        }
        if (changed.Count == 0) return false;

        // From the first write on, a request that goes away no longer stops the change, and a later step that fails does not skip the restart.
        var none = CancellationToken.None;
        await db.SaveChangesAsync(none);
        try
        {
            var stamp = Guid.NewGuid().ToString("N");
            await db.SetAsync(RuntimeSettingKeys.PluginSettingsStamp, stamp, by, now, none);
            _watcher.SawStamp(stamp);
            await ReloadAsync(db, none);
            if (restart) _watcher.Saw(await PluginGeneration.RaiseAsync(db, by, now, none));
            // The keys that changed, never a value.
            await scope.ServiceProvider.GetRequiredService<IAuditLog>().AppendAsync(new AuditEvent(AuditActions.PluginSettingsChange, by, "plugin", pluginId,
                Data: new Dictionary<string, string> { ["keys"] = string.Join(", ", changed.Order(StringComparer.Ordinal)) }), none);
        }
        finally
        {
            if (restart) _restarter.Restart($"A setting of {pluginId} changed.");
        }
        return restart;
    }

    /// <summary>Reads the values again. Called when another server changed a setting.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await ReloadAsync(scope.ServiceProvider.GetRequiredService<KbDbContext>(), ct);
    }

    private async Task ReloadAsync(KbDbContext db, CancellationToken ct) =>
        _snapshot.Replace((await db.PluginSettingValues.AsNoTracking().Where(v => !v.Secret).ToListAsync(ct)).Select(v => (v.PluginId, v.Key, v.Value)));

    private static string? Refusal(PluginSetting d, string value) => d.Type switch
    {
        "number" when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => "A number.",
        "toggle" when value is not ("true" or "false") => "true or false.",
        "select" when d.Options is { } options && !options.Contains(value) => "Not one of the choices.",
        _ => null,
    };
}

public sealed record PluginSettingsDto(IReadOnlyDictionary<string, string?> Values, IReadOnlyList<string> SecretsSet);
public sealed record PluginSettingsRequest(Dictionary<string, string?>? Values);
