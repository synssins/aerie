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

/// <summary>
/// Every 5 seconds reads the plugin generation from the database. When another server changed the plugins,
/// the number is higher than the one this server knows, and this server restarts to load the same set.
/// </summary>
public sealed class PluginWatcher(IServiceScopeFactory scopes, PluginRegistry registry, IAppRestarter restarter, TimeProvider time, ILogger<PluginWatcher> log) : BackgroundService
{
    private long _known = registry.Generation;

    /// <summary>Called by this server after it made a change itself: it already asked for its restart.</summary>
    public void Saw(long generation)
    {
        long known;
        while ((known = Interlocked.Read(ref _known)) < generation && Interlocked.CompareExchange(ref _known, generation, known) != known) { }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var value = await scope.ServiceProvider.GetRequiredService<KbDbContext>().GetAsync(RuntimeSettingKeys.PluginGeneration, ct);
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
