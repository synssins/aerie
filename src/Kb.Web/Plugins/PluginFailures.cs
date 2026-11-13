using Kb.Core.Plugins;
using Kb.Plugins;

namespace Kb.Web.Plugins;

/// <summary>
/// Counts the failures of each loaded plugin on this server. Five within ten minutes and the core turns the plugin off,
/// with the place and the first line of the last error as the reason. The count is a rate, not a record, so losing it at a restart is fine.
/// </summary>
public sealed class PluginFailures(IServiceScopeFactory scopes, PluginRegistry registry, TimeProvider time, ILogger<PluginFailures> log) : IPluginFailures
{
    public const int Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, List<DateTimeOffset>> _recent = new(StringComparer.Ordinal);

    /// <summary>The disable started by the last report that reached the limit. Tests wait for it.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public void Report(string pluginId, string where, Exception? exception = null)
    {
        // Only a plugin this server loaded can be turned off for failing. The core's own parts report under "core".
        if (registry.Find(pluginId) is not { Loaded: true }) return;
        log.LogWarning(exception, "Plugin {Plugin} failed in {Where}.", pluginId, where);
        var now = time.GetUtcNow();
        lock (_recent)
        {
            if (!_recent.TryGetValue(pluginId, out var times)) _recent[pluginId] = times = [];
            times.RemoveAll(t => now - t >= Window);
            times.Add(now);
            if (times.Count < Limit) return;
            times.Clear();
            var reason = exception is null ? where : $"{where}: {exception.Message.Split('\r', '\n')[0]}";
            Pending = Task.Run(() => DisableAsync(pluginId, reason));
        }
    }

    private async Task DisableAsync(string pluginId, string reason)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PluginInstaller>().DisableByCoreAsync(pluginId, reason, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Could not disable plugin {Plugin}.", pluginId); }
    }
}
