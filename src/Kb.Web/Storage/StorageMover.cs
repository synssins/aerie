using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// Copies every file of a move to the new place, then compares every file's size in both places.
/// One server copies at a time: it claims the move for 2 minutes and renews the claim as it works; another server takes over when a claim runs out.
/// Nothing is ever deleted from either place.
/// </summary>
public sealed class StorageMover(IServiceScopeFactory scopes, ActiveFileStore store, TimeProvider time, ILogger<StorageMover> log) : BackgroundService
{
    private static readonly TimeSpan ClaimLife = TimeSpan.FromMinutes(2);
    // A start on this server wakes it at once. The wait only matters for taking over from a server that stopped.
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private const int ProgressEvery = 50;

    private readonly string _me = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _wake = new(0);

    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Storage move failed."); }
                await _wake.WaitAsync(Poll, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    internal async Task<bool> ClaimAsync(Guid moveId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ClaimAsync(scope.ServiceProvider.GetRequiredService<KbDbContext>(), moveId, ct);
    }

    /// <summary>One conditional update: only one server's claim can succeed.</summary>
    private async Task<bool> ClaimAsync(KbDbContext db, Guid moveId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var until = now + ClaimLife;
        return await db.StorageMoves
            .Where(m => m.Id == moveId && m.State == StorageMoveState.Copying && (m.ClaimedBy == _me || m.ClaimedUntil == null || m.ClaimedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClaimedBy, _me).SetProperty(m => m.ClaimedUntil, until), ct) == 1;
    }

    /// <summary>Copies and checks the move that is waiting, if this server can claim it. False when there was nothing for this server to do.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var move = await db.StorageMoves.AsNoTracking().Where(m => m.State == StorageMoveState.Copying).OrderBy(m => m.StartedAt).FirstOrDefaultAsync(ct);
        if (move is null || !await ClaimAsync(db, move.Id, ct)) return false;

        var places = await db.StoragePlaces.AsNoTracking().Where(p => p.Id == move.FromId || p.Id == move.ToId).ToListAsync(ct);
        var from = store.Open(places.Single(p => p.Id == move.FromId));
        var to = store.Open(places.Single(p => p.Id == move.ToId));

        var copied = new HashSet<Guid>();
        var failed = new HashSet<Guid>();
        var checkedCount = 0;
        string? lastError = null;

        // Writes the counts and renews the claim. False: the move was cancelled or another server took it over, so this one stops.
        async Task<bool> ProgressAsync(int total, StorageMoveState state)
        {
            var until = time.GetUtcNow() + ClaimLife;
            int c = copied.Count, f = failed.Count, k = checkedCount;
            var error = lastError;
            return await db.StorageMoves.Where(m => m.Id == move.Id && m.State == StorageMoveState.Copying && m.ClaimedBy == _me)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Total, total).SetProperty(m => m.Copied, c).SetProperty(m => m.Checked, k).SetProperty(m => m.Failed, f)
                    .SetProperty(m => m.LastError, error).SetProperty(m => m.State, state).SetProperty(m => m.ClaimedUntil, until), ct) == 1;
        }

        void Fail(Guid id, string why)
        {
            failed.Add(id);
            var text = $"{id}: {why}";
            lastError = text[..Math.Min(text.Length, 500)];
        }

        // Copy. A file already in the new place with the same size is skipped.
        var ids = await FileIdsAsync(db, ct);
        if (!await ProgressAsync(ids.Count, StorageMoveState.Copying)) return true;
        for (var i = 0; i < ids.Count; i++)
        {
            var key = ids[i].ToString("N");
            try
            {
                var size = await from.SizeAsync(key, ct);
                if (size is null) Fail(ids[i], "Not found.");
                else
                {
                    if (await to.SizeAsync(key, ct) != size)
                    {
                        await using var source = await from.OpenAsync(key, ct) ?? throw new FileNotFoundException("Not found.");
                        await to.SaveAsync(key, source, long.MaxValue, ct);
                    }
                    copied.Add(ids[i]);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { Fail(ids[i], StorageRoutes.FirstLine(e.Message)); }
            if ((i + 1) % ProgressEvery == 0 && !await ProgressAsync(ids.Count, StorageMoveState.Copying)) return true;
        }

        // Check. The files are listed again, so one uploaded or removed while the copy ran is counted as it is now.
        var all = await FileIdsAsync(db, ct);
        copied.IntersectWith(all);
        failed.IntersectWith(all);
        for (var i = 0; i < all.Count; i++)
        {
            if (!failed.Contains(all[i]))
            {
                var key = all[i].ToString("N");
                try
                {
                    var size = await from.SizeAsync(key, ct);
                    if (size is null) Fail(all[i], "Not found.");
                    else if (await to.SizeAsync(key, ct) != size) Fail(all[i], "The size differs in the new place.");
                    else
                    {
                        copied.Add(all[i]);
                        checkedCount++;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { Fail(all[i], StorageRoutes.FirstLine(e.Message)); }
                if (failed.Contains(all[i])) copied.Remove(all[i]);
            }
            if ((i + 1) % ProgressEvery == 0 && !await ProgressAsync(all.Count, StorageMoveState.Copying)) return true;
        }
        await ProgressAsync(all.Count, StorageMoveState.Copied);
        return true;
    }

    /// <summary>
    /// Every stored file: images, attachments including those removed from their page (their files are kept), and the older versions of
    /// attachments kept by the Data Center compatible surface.
    /// </summary>
    private static async Task<List<Guid>> FileIdsAsync(KbDbContext db, CancellationToken ct) =>
        [.. await db.SpaceImages.AsNoTracking().Select(i => i.Id).ToListAsync(ct), .. await db.Attachments.AsNoTracking().Select(a => a.Id).ToListAsync(ct),
            .. await db.DcAttachmentVersions.AsNoTracking().Where(v => v.BlobId != v.AttachmentId).Select(v => v.BlobId).ToListAsync(ct)];
}
