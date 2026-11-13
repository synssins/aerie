using Kb.Core.Plugins;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Pipeline;

/// <summary>
/// Runs the jobs of the loaded plugins on their schedules. Every 30 seconds it looks for a job that is due, claims it in the database
/// (so two servers never run one together), runs it in its own scope as its plugin, and sets its next slot.
/// A job that throws has its error recorded and reported as a failure of its plugin; it runs again at its next slot.
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IRegistrations registrations, PluginRegistry registry, IPluginFailures failures, TimeProvider time, ILogger<JobRunner> log)
    : BackgroundService
{
    private static readonly TimeSpan ShortestClaim = TimeSpan.FromMinutes(5);
    private readonly string _me = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "The job runner failed; it tries again."); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Runs every job that is due and that this server can claim. Returns how many ran.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var ran = 0;
        foreach (var job in registrations.All<JobReg>())
        {
            if (registry.Find(job.PluginId)?.Manifest.Schedules.FirstOrDefault(s => s.Name == job.Value.Name) is not { } schedule) continue;
            var every = TimeSpan.FromMinutes(Math.Max(1, schedule.EveryMinutes));
            if (!await ClaimAsync(job.PluginId, job.Value.Name, every, ct)) continue;

            string? error = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ActorAccessor>().Use(Actor.Plugin(job.PluginId, "job"));
                await ((IKbJob)ActivatorUtilities.CreateInstance(scope.ServiceProvider, job.Value.Job)).RunAsync(ct);
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                error = e.Message.Split('\r', '\n')[0];
                error = error[..Math.Min(error.Length, 500)];
                log.LogError(e, "Job {Job} of {Plugin} failed.", job.Value.Name, job.PluginId);
                failures.Report(job.PluginId, $"job {job.Value.Name}", e);
            }

            // The next slot counts from the end of the run, and the claim is given back. Only while this server still holds it.
            var now = time.GetUtcNow();
            var (plugin, name, next) = (job.PluginId, job.Value.Name, now + every);
            await using var done = scopes.CreateAsyncScope();
            await done.ServiceProvider.GetRequiredService<KbDbContext>().JobRuns.Where(r => r.PluginId == plugin && r.Name == name && r.ClaimedBy == _me)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastRunAt, now).SetProperty(r => r.NextRunAt, next).SetProperty(r => r.LastError, error)
                    .SetProperty(r => r.ClaimedBy, (string?)null).SetProperty(r => r.ClaimedUntil, (DateTimeOffset?)null), CancellationToken.None);
            ran++;
        }
        return ran;
    }

    /// <summary>True when the job is due and this server now holds it. A job seen for the first time is due at once.</summary>
    private async Task<bool> ClaimAsync(string plugin, string name, TimeSpan every, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var now = time.GetUtcNow();
        if (!await db.JobRuns.AnyAsync(r => r.PluginId == plugin && r.Name == name, ct))
        {
            db.JobRuns.Add(new JobRun { PluginId = plugin, Name = name, NextRunAt = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { } // another server added the row at the same moment; the claim below decides who runs
        }
        // A claim outlives a server that died: it ends by itself after the interval, five minutes at least.
        var until = now + (every > ShortestClaim ? every : ShortestClaim);
        return await db.JobRuns.Where(r => r.PluginId == plugin && r.Name == name && r.NextRunAt <= now && (r.ClaimedUntil == null || r.ClaimedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ClaimedBy, _me).SetProperty(r => r.ClaimedUntil, until), ct) == 1;
    }
}
