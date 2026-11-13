using System.Text.Json;
using Kb.Core.Audit;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// Ships audit entries to every enabled log target: the local file, syslog, an HTTPS collector, and kinds plugins bring.
/// Each target keeps the sequence it got to, so entries go out in order, an unreachable target is retried from where it stopped,
/// and a restart loses nothing. One server ships to a target at a time; another takes over when its claim runs out.
/// </summary>
public sealed class LogShipperService(IServiceScopeFactory scopes, IEnumerable<ILogShipperKind> kinds, IDataProtectionProvider protection, TimeProvider time, ILogger<LogShipperService> log)
    : BackgroundService
{
    public const string SecretPurpose = "kb.logship.secret";
    private static readonly Guid FirstTargetId = new("5f1e7c0a-0000-4000-8000-000000000002");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    // An entry written on this server wakes it at once. The wait covers entries written on another server.
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private const int Batch = 200;

    private readonly IDataProtector _protector = protection.CreateProtector(SecretPurpose);
    private readonly string _me = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _wake = new(0);

    public IReadOnlyList<ILogShipperKind> Kinds { get; } = [.. kinds];

    public ILogShipperKind? Kind(string? kind) => Kinds.FirstOrDefault(k => k.Kind == kind);

    public void Wake() => _wake.Release();

    /// <summary>When no target exists, the local log file is made: the baseline that needs no other system.</summary>
    public static async Task EnsureFirstTargetAsync(KbDbContext db, string folder, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.LogTargets.AnyAsync(ct)) return;
        var target = new LogTarget
        {
            Id = FirstTargetId, Name = "Local log file", Kind = FileLogShipperKind.Name, CreatedAt = now,
            SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = folder, ["days"] = "90" }),
        };
        db.LogTargets.Add(target);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.Entry(target).State = EntityState.Detached; } // the other server made it first
    }

    public Dictionary<string, string> Secrets(LogTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.SecretsProtected is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(target.SecretsProtected)) ?? [];
    }

    public string? Protect(IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return secrets.Count == 0 ? null : _protector.Protect(JsonSerializer.Serialize(secrets));
    }

    public ILogShipper Open(LogTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var kind = Kind(target.Kind) ?? throw new InvalidOperationException($"Log target kind '{target.Kind}' is not available.");
        var settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(target.SettingsJson) ?? [];
        foreach (var (k, v) in Secrets(target)) settings[k] = v;
        return kind.Open(settings);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Log shipping failed."); }
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

    internal async Task<bool> ClaimAsync(Guid targetId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ClaimAsync(scope.ServiceProvider.GetRequiredService<KbDbContext>(), targetId, ct);
    }

    private async Task<bool> ClaimAsync(KbDbContext db, Guid targetId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var until = now + Lease;
        return await db.LogTargets.Where(t => t.Id == targetId && (t.ClaimedBy == _me || t.ClaimedUntil == null || t.ClaimedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedBy, _me).SetProperty(t => t.ClaimedUntil, until), ct) == 1;
    }

    /// <summary>Sends what every enabled target is missing. Returns how many entries were accepted, over all targets.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var shipped = 0;
        foreach (var target in await db.LogTargets.AsNoTracking().Where(t => t.Enabled).ToListAsync(ct))
        {
            var last = target.LastSequence;
            if (!await db.AuditEntries.AnyAsync(a => a.Sequence > last, ct) || !await ClaimAsync(db, target.Id, ct)) continue;
            try
            {
                var shipper = Open(target);
                while (true)
                {
                    var batch = await db.AuditEntries.AsNoTracking().Where(a => a.Sequence > last).OrderBy(a => a.Sequence).Take(Batch).ToListAsync(ct);
                    if (batch.Count == 0) break;
                    await shipper.SendAsync(await EventsAsync(db, batch, ct), ct);
                    last = batch[^1].Sequence;
                    var now = time.GetUtcNow();
                    var until = now + Lease;
                    // Moves the mark only while this server still holds the claim; otherwise another server has taken over and this one stops.
                    if (await db.LogTargets.Where(t => t.Id == target.Id && t.ClaimedBy == _me).ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.LastSequence, last).SetProperty(t => t.LastShippedAt, now).SetProperty(t => t.LastError, (string?)null)
                            .SetProperty(t => t.LastErrorAt, (DateTimeOffset?)null).SetProperty(t => t.ClaimedUntil, until), ct) == 0) break;
                    shipped += batch.Count;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var error = StorageRoutes.FirstLine(e.Message);
                error = error[..Math.Min(error.Length, 500)];
                var now = time.GetUtcNow();
                await db.LogTargets.Where(t => t.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastError, error).SetProperty(t => t.LastErrorAt, now), ct);
            }
        }
        return shipped;
    }

    private static async Task<List<LogEvent>> EventsAsync(KbDbContext db, List<AuditEntry> batch, CancellationToken ct)
    {
        var ids = batch.Select(a => a.ActorId).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        return [.. batch.Select(a => new LogEvent
        {
            Sequence = a.Sequence, At = a.At, Action = a.Action, ActorId = a.ActorId, Actor = a.ActorId is { } id ? names.GetValueOrDefault(id) : null,
            TargetType = a.TargetType, TargetId = a.TargetId, SpaceId = a.SpaceId, Hash = a.Hash, Server = Environment.MachineName,
            Data = JsonSerializer.Deserialize<Dictionary<string, string>>(a.DataJson) ?? [],
        })];
    }
}

/// <summary>Wakes the shipper when an entry is written on this server, so it leaves within moments and not at the next poll.</summary>
public sealed class LogShipSignal(LogShipperService shipper) : IAuditSink
{
    public string ProviderId => "core";

    public Task ForwardAsync(AuditRecord record, CancellationToken ct)
    {
        shipper.Wake();
        return Task.CompletedTask;
    }
}
