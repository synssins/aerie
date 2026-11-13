using Kb.Plugins;
using Kb.Plugins.Actions;

namespace Kb.TestPlugin.Probe;

/// <summary>A health check that throws, registered when the Register setting is "health".</summary>
public sealed class ProbeHealth : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Probe health failed.");
}

/// <summary>Counts its runs, notes who it ran as, reads a page when JobPage names one, and can wait or throw.</summary>
public sealed class ProbeJob(ProbeState state, IActorAccessor actors, IActionBus bus) : IKbJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (int.TryParse(state.Setting("JobDelay"), out var delay)) await Task.Delay(delay, ct);
        if (state.Setting("FailOn") == "job") throw new InvalidOperationException("Probe job failed.");
        var me = actors.Current;
        state.LastJob = $"{me.Kind}:{me.PluginId}:{me.Source}";
        if (Guid.TryParse(state.Setting("JobPage"), out var page))
            state.LastJobRead = (await bus.SendAsync(new ReadPage { PageId = page }, ct)) is { Ok: true } read ? read.Value!.Title : "refused";
        state.Count("job", "tick");
    }
}
