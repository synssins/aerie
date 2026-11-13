using System.Text.Json;
using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Notifications;
using Kb.Core.Spaces;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kb.Data;

/// <summary>Hash-chained audit log. Appends serialize across instances (SQL Server app lock / SQLite write lock).</summary>
public sealed class SqlAuditLog(KbDbContext db, TimeProvider time, IEnumerable<IAuditSink> sinks, IPluginFailures failures, ILogger<SqlAuditLog> log) : IAuditLog
{
    private static readonly SemaphoreSlim LocalGate = new(1, 1);

    public async Task AppendAsync(AuditEvent e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        AuditRecord record;
        await LocalGate.WaitAsync(ct);
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            record = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
                if (db.Database.IsSqlServer())
                    await db.Database.ExecuteSqlRawAsync(
                        "EXEC sp_getapplock @Resource = 'kb-audit-chain', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000", ct);

                var last = await db.AuditEntries.AsNoTracking().OrderByDescending(a => a.Sequence).Select(a => new { a.Sequence, a.Hash }).FirstOrDefaultAsync(ct);
                var seq = (last?.Sequence ?? 0) + 1;
                var prev = last?.Hash ?? AuditHasher.Genesis;
                // Truncate to whole milliseconds: SQLite's binary DateTimeOffset storage keeps only 0.1 ms, and the hash must round-trip.
                var now = time.GetUtcNow();
                now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
                var data = e.Data ?? new Dictionary<string, string>();
                var hash = AuditHasher.Hash(prev, seq, now, e);
                var entry = new AuditEntry
                {
                    Sequence = seq, At = now, Action = e.Action, ActorId = e.ActorId, TargetType = e.TargetType, TargetId = e.TargetId,
                    SpaceId = e.SpaceId, DataJson = JsonSerializer.Serialize(data), PrevHash = prev, Hash = hash,
                };
                db.AuditEntries.Add(entry);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                // Only the entry is let go. The context is the request's: clearing it all would detach rows a handler loaded and is about
                // to change (an action sent from a plugin's hook writes its audit entry in the middle of the action it hooks).
                db.Entry(entry).State = EntityState.Detached;
                return new AuditRecord(seq, now, e.Action, e.ActorId, e.TargetType, e.TargetId, e.SpaceId, data, hash);
            });
        }
        finally { LocalGate.Release(); }

        foreach (var sink in sinks)
        {
            try { await sink.ForwardAsync(record, ct); }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // The entry is already written; a sink cannot undo or silence it. A sink that times out throws a cancellation too: that is its failure.
                log.LogError(ex, "Audit sink {Sink} failed", sink.ProviderId);
                failures.Report(sink.ProviderId, "audit sink", ex);
            }
        }
    }

    public async Task<AuditVerifyResult> VerifyAsync(CancellationToken ct = default)
    {
        var prev = AuditHasher.Genesis;
        long expected = 1;
        await foreach (var a in db.AuditEntries.AsNoTracking().OrderBy(a => a.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(a.DataJson) ?? [];
            var ev = new AuditEvent(a.Action, a.ActorId, a.TargetType, a.TargetId, a.SpaceId, data);
            if (a.Sequence != expected || a.PrevHash != prev || AuditHasher.Hash(prev, a.Sequence, a.At, ev) != a.Hash)
                return new AuditVerifyResult(false, a.Sequence);
            prev = a.Hash;
            expected++;
        }
        return new AuditVerifyResult(true, null);
    }
}

public sealed class SpaceAccessData(KbDbContext db, Microsoft.Extensions.Options.IOptions<Kb.Core.Config.KbOptions> options) : ISpaceAccessData
{
    public async Task<PrincipalInfo?> GetPrincipalAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null || u.DeactivatedAt is not null) return null;
        var groups = await db.KbGroupMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Join(db.KbGroups, m => m.GroupId, g => g.Id, (m, g) => g.Key).ToListAsync(ct);
        return new PrincipalInfo(u.Id, u.IsAdmin, await HasFullAccessAsync(u, groups, ct), groups);
    }

    /// <summary>Local and break-glass accounts have full access; directory users need membership in an allowed group.</summary>
    private Task<bool> HasFullAccessAsync(KbUser u, List<string> groupKeys, CancellationToken _)
    {
        if (u.Source == UserSource.Local || u.IsBreakGlass) return Task.FromResult(true);
        var allowed = options.Value.Auth.AllowedGroupIds;
        return Task.FromResult(groupKeys.Intersect(allowed, StringComparer.OrdinalIgnoreCase).Any());
    }

    public Task<Space?> GetSpaceAsync(Guid spaceId, CancellationToken ct) => db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId, ct);

    public async Task<IReadOnlyList<Space>> GetAllSpacesAsync(CancellationToken ct) => await db.Spaces.AsNoTracking().ToListAsync(ct);

    public async Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsAsync(Guid spaceId, CancellationToken ct) =>
        await db.SpaceRoleAssignments.AsNoTracking().Where(a => a.SpaceId == spaceId).ToListAsync(ct);

    public async Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsForPrincipalsAsync(IReadOnlyCollection<string> userAndGroupIds, CancellationToken ct) =>
        await db.SpaceRoleAssignments.AsNoTracking().Where(a => userAndGroupIds.Contains(a.PrincipalId)).ToListAsync(ct);

    public async Task<IReadOnlyList<SpaceAccessGrant>> GetActiveGrantsAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        // Filter expiry client-side: keeps the comparison identical across providers.
        var grants = await db.SpaceAccessGrants.AsNoTracking().Where(g => g.UserId == userId).ToListAsync(ct);
        return grants.Where(g => g.ExpiresAt is null || g.ExpiresAt > now).ToList();
    }

    public async Task<bool> IsPublicSpaceEnabledAsync(CancellationToken ct) =>
        (await db.RuntimeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == RuntimeSettingKeys.PublicSpaceEnabled, ct))?.Value == "true";
}

public sealed class AccessData(KbDbContext db) : IAccessData
{
    public async Task AddRequestAsync(AccessRequest r, CancellationToken ct) { db.AccessRequests.Add(r); await db.SaveChangesAsync(ct); }
    public Task<AccessRequest?> GetRequestAsync(Guid id, CancellationToken ct) => db.AccessRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
    public Task SaveRequestAsync(AccessRequest r, CancellationToken ct) => db.SaveChangesAsync(ct);
    public async Task AddGrantAsync(SpaceAccessGrant g, CancellationToken ct) { db.SpaceAccessGrants.Add(g); await db.SaveChangesAsync(ct); }

    public async Task AddAssignmentAsync(SpaceRoleAssignment a, CancellationToken ct)
    {
        var existing = await db.SpaceRoleAssignments.FirstOrDefaultAsync(x => x.SpaceId == a.SpaceId && x.PrincipalType == a.PrincipalType && x.PrincipalId == a.PrincipalId, ct);
        if (existing is null) db.SpaceRoleAssignments.Add(a);
        else if (a.Role > existing.Role) existing.Role = a.Role;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetOwnerUserIdsAsync(Guid spaceId, CancellationToken ct)
    {
        var ids = await db.SpaceRoleAssignments.AsNoTracking()
            .Where(a => a.SpaceId == spaceId && a.Role == SpaceRole.Owner && a.PrincipalType == PrincipalType.User)
            .Select(a => a.PrincipalId).ToListAsync(ct);
        return ids.Select(Guid.Parse).ToList();
    }
}

public sealed class NotificationStore(KbDbContext db) : INotificationStore
{
    public async Task AddAsync(IEnumerable<Notification> items, CancellationToken ct) { db.Notifications.AddRange(items); await db.SaveChangesAsync(ct); }
}

public sealed class UserDirectory(KbDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyList<string>> GetEmailsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.Email != null).Select(u => u.Email!).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetAdminIdsAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.IsAdmin && u.DeactivatedAt == null).Select(u => u.Id).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, NotifyPref>> GetNotifyPrefsAsync(IReadOnlyCollection<Guid> userIds, string kind, CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.SettingsJson }).ToListAsync(ct);
        var result = new Dictionary<Guid, NotifyPref>();
        foreach (var r in rows)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.SettingsJson);
                if (doc.RootElement.TryGetProperty("notify", out var n) && n.TryGetProperty(kind, out var p))
                    result[r.Id] = new NotifyPref(p.TryGetProperty("inApp", out var i) && i.GetBoolean(), p.TryGetProperty("email", out var e) && e.GetBoolean());
            }
            catch (System.Text.Json.JsonException) { /* unreadable settings: fall back to defaults */ }
        }
        return result;
    }
}
