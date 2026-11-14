using Kb.Core.Ai;
using Kb.Core.Import;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Import;

/// <summary>
/// Works through imports in the background: reads and groups new batches, then drafts each queued item. One item at a time, so
/// one bad file fails only its item. It keeps to each person's AI calls a minute by waiting for a free slot, not by being refused.
/// Kb:Import:PollSeconds 0 turns it off (tests run it by hand with <see cref="RunOnceAsync"/>).
/// </summary>
public sealed class ImportWorker(IServiceScopeFactory scopes, IConfiguration config, AiRateLimiter limiter, ILogger<ImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = config.GetValue("Kb:Import:PollSeconds", 2);
        if (seconds <= 0) return;
        await ResumeAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (!await RunOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
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

    /// <summary>One step: a batch to group, or else one item to draft. True when it did something.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var imports = scope.ServiceProvider.GetRequiredService<ImportService>();
        var ai = scope.ServiceProvider.GetRequiredService<AiService>();

        var batch = await db.ImportBatches.Where(b => !b.Grouped && b.Error == null && b.CompletedAt != null).OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (batch is not null)
        {
            try { await imports.GroupBatchAsync(batch, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Import batch {Batch} could not be read.", batch.Id);
                batch.Error = "The files could not be read.";
                await db.SaveChangesAsync(ct);
            }
            return true;
        }

        var per = (await ai.SettingsAsync(ct)).UserPerMinute;
        var queued = await db.ImportItems.Where(i => i.Status == ImportStatus.Queued).OrderBy(i => i.CreatedAt).Take(50).ToListAsync(ct);
        var item = queued.FirstOrDefault(i => limiter.Free($"u:{i.UserId}", per));
        if (item is null) return false;
        item.Status = ImportStatus.Working;
        item.Attempts++;
        await db.SaveChangesAsync(ct);
        try { await imports.ProposeAsync(item, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Import item {Item} could not be drafted.", item.Id);
            (item.Status, item.Error) = (ImportStatus.Failed, "The draft could not be made.");
        }
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

}
