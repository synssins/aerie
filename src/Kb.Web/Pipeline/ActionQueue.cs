using System.Threading.Channels;
using Kb.Plugins;
using Kb.Plugins.Actions;

namespace Kb.Web.Pipeline;

/// <summary>
/// Runs after handlers, one at a time, in the order their actions succeeded. Each runs in its own scope, as the plugin that owns it.
/// One that throws is reported and the next one runs; the action it follows has already succeeded and is not undone.
/// shortcut: the queue is in memory, so a handler is lost if the process dies before it ran; move it to the database when a plugin needs that.
/// A normal stop loses nothing: what is still waiting runs before the app goes down.
/// </summary>
public sealed class ActionQueueWorker(IServiceScopeFactory scopes, IPluginFailures failures, ILogger<ActionQueueWorker> log) : BackgroundService
{
    private sealed record Work(string PluginId, string Where, Func<IServiceProvider, CancellationToken, Task> Run);

    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private long _lastId;

    public long NextId() => Interlocked.Increment(ref _lastId);

    public void Enqueue(string pluginId, string where, Func<IServiceProvider, CancellationToken, Task> run) => _queue.Writer.TryWrite(new Work(pluginId, where, run));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A handler that is running when the app stops finishes; stopping only ends the waiting for the next one.
            await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken)) await RunAsync(work, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out var work)) await RunAsync(work, cancellationToken);
    }

    private async Task RunAsync(Work work, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActorAccessor>().Use(Actor.Plugin(work.PluginId, "queue"));
            await work.Run(scope.ServiceProvider, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "{Where} of {Plugin} failed.", work.Where, work.PluginId);
            failures.Report(work.PluginId, work.Where, e);
        }
    }
}
