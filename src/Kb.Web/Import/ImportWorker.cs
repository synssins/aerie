using System.Diagnostics;
using Kb.Core.Ai;
using Kb.Core.Import;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Import;

/// <summary>
/// Works through imports in the background: reads and groups new batches, then drafts queued items, up to the AI settings'
/// ImportConcurrency at once. Each item is claimed in the database first, so two servers never draft the same one, and one bad
/// file fails only its item, with the reason in plain words. It keeps to each person's AI calls a minute by waiting for a free
/// slot, not by being refused. Kb:Import:PollSeconds 0 turns it off (tests run it by hand with <see cref="RunOnceAsync"/>).
/// </summary>
public sealed class ImportWorker(IServiceScopeFactory scopes, IConfiguration config, AiRateLimiter limiter, ILogger<ImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = config.GetValue("Kb:Import:PollSeconds", 2);
        if (seconds <= 0) return;
        await ResumeAsync(stoppingToken);
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                running.RemoveAll(t => t.IsCompleted);
                if (await GroupOneAsync(stoppingToken)) continue;
                var limit = await ConcurrencyAsync(stoppingToken);
                var started = false;
                while (running.Count < limit && await ClaimAsync(stoppingToken) is { } id)
                {
                    running.Add(Task.Run(() => DraftAsync(id, stoppingToken), stoppingToken));
                    started = true;
                }
                if (!started)
                {
                    var wait = Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    await (running.Count > 0 ? Task.WhenAny([wait, .. running]) : wait);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Import worker step failed."); await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
        }
    }

    /// <summary>Items a stopped server was drafting start again.</summary>
    private async Task ResumeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        await db.ImportItems.Where(i => i.Status == ImportStatus.Working).ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ImportStatus.Queued), ct);
    }

    private async Task<int> ConcurrencyAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return Math.Clamp((await scope.ServiceProvider.GetRequiredService<AiService>().SettingsAsync(ct)).ImportConcurrency, 1, 8);
    }

    /// <summary>One step, in turn (what the tests drive): a batch to group, or else one item to draft. True when it did something.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (await GroupOneAsync(ct)) return true;
        if (await ClaimAsync(ct) is not { } id) return false;
        await DraftAsync(id, ct);
        return true;
    }

    private async Task<bool> GroupOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var batch = await db.ImportBatches.Where(b => !b.Grouped && b.Error == null && b.CompletedAt != null).OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (batch is null) return false;
        try { await scope.ServiceProvider.GetRequiredService<ImportService>().GroupBatchAsync(batch, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Import batch {Batch} could not be read.", batch.Id);
            batch.Error = $"The files could not be read: {Reason(e)}";
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>The next queued item whose person has an AI call free, set to Working only if no one else took it first.</summary>
    private async Task<Guid?> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var per = (await scope.ServiceProvider.GetRequiredService<AiService>().SettingsAsync(ct)).UserPerMinute;
        var queued = await db.ImportItems.AsNoTracking().Where(i => i.Status == ImportStatus.Queued).OrderBy(i => i.CreatedAt).Take(50).Select(i => new { i.Id, i.UserId }).ToListAsync(ct);
        foreach (var q in queued.Where(q => limiter.Free($"u:{q.UserId}", per)))
        {
            var now = DateTimeOffset.UtcNow;
            var took = await db.ImportItems.Where(i => i.Id == q.Id && i.Status == ImportStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ImportStatus.Working).SetProperty(i => i.UpdatedAt, now).SetProperty(i => i.Attempts, i => i.Attempts + 1), ct);
            if (took == 1) return q.Id;
        }
        return null;
    }

    private async Task DraftAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var item = await db.ImportItems.FirstAsync(i => i.Id == id, ct);
        var clock = Stopwatch.StartNew();
        try { await scope.ServiceProvider.GetRequiredService<ImportService>().ProposeAsync(item, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Import item {Item} could not be drafted.", item.Id);
            (item.Status, item.Error) = (ImportStatus.Failed, $"The draft could not be made: {Reason(e)}");
        }
        log.LogInformation("Import item {Item} drafted in {Ms} ms: {Status}.", item.Id, clock.ElapsedMilliseconds, item.Status);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>An error in plain words for the importer: what happened, never a stack trace.</summary>
    internal static string Reason(Exception e) => e switch
    {
        OperationCanceledException or TimeoutException => "the model took too long to answer.",
        HttpRequestException => "the model server could not be reached.",
        System.Text.Json.JsonException => "the model's reply was not readable.",
        OutOfMemoryException or InsufficientExecutionStackException => "the file is too large or too deeply nested to work with.",
        DbUpdateException => "the draft could not be saved.",
        _ => $"an error on the server ({e.GetType().Name}). Draft again, or ask an Admin to check the server log.",
    };
}
