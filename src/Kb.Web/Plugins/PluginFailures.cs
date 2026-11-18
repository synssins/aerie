using Kb.Core.Plugins;
using Kb.Plugins;

namespace Kb.Web.Plugins;

/// <summary>
/// Counts the failures of each loaded plugin on this server. Five within ten minutes and the core turns the plugin off,
/// with the place and the first line of the last error as the reason. The count is a rate, not a record, so losing it at a restart is fine.
/// Failures in the requests of one person who is not an Admin count once, however many there are: turning a plugin off restarts the app
/// and can lock what the plugin guards, and that is not for any one user to cause with input the plugin chokes on. An Admin's failures
/// and those of work without a caller (jobs, the queue, imports) count each.
/// </summary>
public sealed class PluginFailures(IServiceScopeFactory scopes, PluginRegistry registry, TimeProvider time, ILogger<PluginFailures> log, IHttpContextAccessor? http = null) : IPluginFailures
{
    public const int Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, List<(DateTimeOffset At, Guid? Caller)>> _recent = new(StringComparer.Ordinal);

    /// <summary>The disable started by the last report that reached the limit. Tests wait for it.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public void Report(string pluginId, string where, Exception? exception = null)
    {
        // Only a plugin this server loaded can be turned off for failing. The core's own parts report under "core".
        if (registry.Find(pluginId) is not { Loaded: true }) return;
        log.LogWarning(exception, "Plugin {Plugin} failed in {Where}.", pluginId, where);
        var caller = Guid.TryParse(http?.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : (Guid?)null;
        var reason = exception is null ? where : $"{where}: {exception.Message.Split('\r', '\n')[0]}";
        var (before, now) = (Pending, time.GetUtcNow());
        Pending = Task.Run(async () =>
        {
            await before; // reports are counted in the order they came
            // Null: counts each time (no caller, or an Admin). Else the one person whose failures count once.
            var one = caller is { } user && !await IsAdminAsync(user) ? caller : null;
            lock (_recent)
            {
                if (!_recent.TryGetValue(pluginId, out var times)) _recent[pluginId] = times = [];
                times.RemoveAll(t => now - t.At >= Window);
                times.Add((now, one));
                if (times.Count(t => t.Caller is null) + times.Where(t => t.Caller is not null).Select(t => t.Caller).Distinct().Count() < Limit) return;
                times.Clear();
            }
            await DisableAsync(pluginId, reason);
        });
    }

    private async Task<bool> IsAdminAsync(Guid user)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(scope.ServiceProvider.GetRequiredService<Kb.Data.KbDbContext>().Users, u => u.Id == user && u.IsAdmin && u.DeactivatedAt == null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
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
