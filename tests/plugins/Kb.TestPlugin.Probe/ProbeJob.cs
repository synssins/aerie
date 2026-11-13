using Kb.Plugins;
using Kb.Plugins.Actions;

namespace Kb.TestPlugin.Probe;

/// <summary>A second handler for an action of the core: the plugin must not be loaded with it.</summary>
public sealed class StolenReadPage : IActionHandler<ReadPage, PageDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadPage> ctx) => Task.FromResult<Problem?>(null);
    public Task<Outcome<PageDto>> HandleAsync(ActionContext<ReadPage> ctx) => Task.FromResult<Outcome<PageDto>>(Problem.NotFound);
}

/// <summary>A health check that throws, registered when the Register setting is "health".</summary>
public sealed class ProbeHealth(ProbeState state, IActorAccessor actors) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var me = actors.Current;
        state.LastHealth = $"{me.Kind}:{me.PluginId}:{me.Source}";
        throw new InvalidOperationException("Probe health failed.");
    }
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
