using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kb.Plugins;

/// <summary>
/// Work a plugin does on a schedule. The manifest names the job and how often it runs (<c>schedules: [{ name, everyMinutes }]</c>).
/// It runs in its own scope, as the plugin (an actor of kind Plugin, source "job"), on one server at a time.
/// It must tolerate running again: a run that fails, or a server that stops, is followed by the next slot.
/// </summary>
public interface IKbJob
{
    Task RunAsync(CancellationToken ct);
}

public sealed record JobReg(string Name, Type Job);

/// <summary>A check of the plugin's own health (can it reach its remote system?). It is shown on the Plugins screen; it is not part of the app's own readiness.</summary>
public sealed record HealthReg(string Name, Type Check);

public static class JobExtensions
{
    /// <summary>Registers the job the manifest schedules under this name.</summary>
    public static IPluginApp Job<TJob>(this IPluginApp app, string name) where TJob : class, IKbJob
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new JobReg(name, typeof(TJob)));
    }

    public static IPluginApp HealthCheck<TCheck>(this IPluginApp app, string name) where TCheck : class, IHealthCheck
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Add(new HealthReg(name, typeof(TCheck)));
    }
}
