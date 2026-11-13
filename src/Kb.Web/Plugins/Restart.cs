using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Plugins;

/// <summary>Restarts the app so that a plugin change takes effect. IIS and App Service start it again on the next request.</summary>
public interface IAppRestarter
{
    void Restart(string reason);
}

/// <summary>Stops the host a moment later, so the response that asked for the restart is sent first.</summary>
public sealed class HostRestarter(IHostApplicationLifetime lifetime, ILogger<HostRestarter> log) : IAppRestarter
{
    private int _asked;

    public void Restart(string reason)
    {
        if (Interlocked.Exchange(ref _asked, 1) == 1) return;
        log.LogWarning("Restarting: {Reason}", reason);
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
    }
}

/// <summary>The number every plugin change raises. A server that started with a lower one restarts.</summary>
public static class PluginGeneration
{
    public static async Task<long> RaiseAsync(KbDbContext db, Guid? by, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var generation = (long.TryParse(await db.GetAsync(RuntimeSettingKeys.PluginGeneration, ct), out var g) ? g : 0) + 1;
        await db.SetAsync(RuntimeSettingKeys.PluginGeneration, generation.ToString(System.Globalization.CultureInfo.InvariantCulture), by, now, ct);
        return generation;
    }
}

/// <summary>
/// Every 5 seconds reads the plugin generation from the database. When another server changed the plugins,
/// the number is higher than the one this server knows, and this server restarts to load the same set.
/// </summary>
public sealed class PluginWatcher(IServiceScopeFactory scopes, PluginRegistry registry, IAppRestarter restarter, TimeProvider time, ILogger<PluginWatcher> log) : BackgroundService
{
    private long _known = registry.Generation;
    private volatile string? _stamp;

    /// <summary>Called by this server after it made a change itself: it already asked for its restart.</summary>
    public void Saw(long generation)
    {
        long known;
        while ((known = Interlocked.Read(ref _known)) < generation && Interlocked.CompareExchange(ref _known, generation, known) != known) { }
    }

    /// <summary>Called by this server after it saved a setting itself: its own values are already current.</summary>
    public void SawStamp(string stamp) => _stamp = stamp;

    internal async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        // A setting changed on another server: read the values again. The first check always reads, to cover a change made while this server was starting.
        var stamp = await db.GetAsync(RuntimeSettingKeys.PluginSettingsStamp, ct) ?? "";
        if (stamp != _stamp)
        {
            await scope.ServiceProvider.GetRequiredService<PluginSettingsStore>().ReloadAsync(ct);
            _stamp = stamp;
        }
        var value = await db.GetAsync(RuntimeSettingKeys.PluginGeneration, ct);
        if (!long.TryParse(value, out var generation) || generation <= Interlocked.Read(ref _known)) return;
        Saw(generation);
        restarter.Restart("Plugins changed on another server.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await CheckAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Reading the plugin generation failed."); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
