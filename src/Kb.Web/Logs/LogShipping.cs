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

public sealed record LogKindDto(string Kind, string Label, IReadOnlyList<LogShipField> Fields);
public sealed record LogTargetDto(Guid Id, string Name, string Kind, IReadOnlyDictionary<string, string?> Settings, IReadOnlyList<string> SecretsSet, bool Enabled,
    long LastSequence, DateTimeOffset? LastShippedAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset CreatedAt);
/// <summary>Head is the newest audit entry, so the screen can show how far behind a target is.</summary>
public sealed record LogsDto(IReadOnlyList<LogKindDto> Kinds, IReadOnlyList<LogTargetDto> Targets, long Head);
public sealed record LogTargetRequest(string? Name, string? Kind, Dictionary<string, string?>? Settings, Dictionary<string, string?>? Secrets, bool Enabled = true);

/// <summary>Administration > Log shipping. Admins only; every change is audited; no answer carries a secret.</summary>
public static class LogRoutes
{
    public static void MapLogEndpoints(this RouteGroupBuilder api)
    {
        var g = api.AdminGroup().MapGroup("/logs");

        g.MapGet("/", async (KbDbContext db, LogShipperService ship, CancellationToken ct) =>
        {
            var targets = await db.LogTargets.AsNoTracking().ToListAsync(ct);
            var head = await db.AuditEntries.MaxAsync(a => (long?)a.Sequence, ct) ?? 0;
            return Results.Ok(new LogsDto([.. ship.Kinds.Select(k => new LogKindDto(k.Kind, k.Label, k.Fields))], [.. targets.OrderBy(t => t.CreatedAt).Select(t => Dto(ship, t))], head));
        });

        // A new target starts at the newest entry: history is not sent to it.
        g.MapPost("/targets", async (LogTargetRequest req, HttpContext http, KbDbContext db, LogShipperService ship, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (ship.Kind(req.Kind) is not { } kind) return Invalid("kind", "Unknown kind.");
            var target = new LogTarget { Name = "", Kind = kind.Kind, CreatedAt = time.GetUtcNow(), LastSequence = await db.AuditEntries.MaxAsync(a => (long?)a.Sequence, ct) ?? 0 };
            if (Apply(ship, kind, target, req) is { } invalid) return invalid;
            db.LogTargets.Add(target);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(Event(AuditActions.LogTargetAdd, http, target), ct);
            return Results.Created($"/api/v1/admin/logs/targets/{target.Id}", Dto(ship, target));
        });

        g.MapPut("/targets/{id:guid}", async (Guid id, LogTargetRequest req, HttpContext http, KbDbContext db, LogShipperService ship, IAuditLog audit, CancellationToken ct) =>
        {
            var target = await db.LogTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (target is null) return Results.NotFound();
            if (ship.Kind(target.Kind) is not { } kind) return Invalid("kind", "Unknown kind.");
            if (Apply(ship, kind, target, req) is { } invalid) return invalid;
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(Event(AuditActions.LogTargetChange, http, target), ct);
            return Results.Ok(Dto(ship, target));
        });

        // Sends one entry named "logs.test". It is not part of the audit log.
        g.MapPost("/targets/{id:guid}/test", async (Guid id, HttpContext http, KbDbContext db, LogShipperService ship, TimeProvider time, CancellationToken ct) =>
        {
            var target = await db.LogTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (target is null) return Results.NotFound();
            var me = http.User.UserId();
            var name = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.UserName).FirstOrDefaultAsync(ct);
            try
            {
                await ship.Open(target).SendAsync([new LogEvent { Sequence = 0, At = time.GetUtcNow(), Action = "logs.test", ActorId = me, Actor = name, Server = Environment.MachineName }], ct);
                return Results.Ok(new StorageTestDto(true, null));
            }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.Ok(new StorageTestDto(false, StorageRoutes.FirstLine(e.Message))); }
        });

        g.MapDelete("/targets/{id:guid}", async (Guid id, HttpContext http, KbDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var target = await db.LogTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (target is null) return Results.NotFound();
            db.LogTargets.Remove(target);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(Event(AuditActions.LogTargetRemove, http, target), ct);
            return Results.NoContent();
        });
    }

    /// <summary>Puts the request on the target. A secret sent as null is kept; sent empty, it is cleared.</summary>
    private static IResult? Apply(LogShipperService ship, ILogShipperKind kind, LogTarget target, LogTargetRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Invalid("name", "1 to 100 characters.");
        var settings = new Dictionary<string, string?>();
        var secrets = ship.Secrets(target);
        foreach (var f in kind.Fields)
        {
            if (f.Type != "secret")
            {
                if (req.Settings?.GetValueOrDefault(f.Key)?.Trim() is { Length: > 0 } v) settings[f.Key] = v;
            }
            else if (req.Secrets?.GetValueOrDefault(f.Key) is { } secret)
            {
                if (secret.Length == 0) secrets.Remove(f.Key);
                else secrets[f.Key] = secret;
            }
        }
        try { kind.Open(settings.Concat(secrets.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).ToDictionary()); }
        catch (SettingException e) { return Invalid(e.Field, e.Message); }
        target.Name = name;
        target.Enabled = req.Enabled;
        target.SettingsJson = JsonSerializer.Serialize(settings);
        target.SecretsProtected = ship.Protect(secrets);
        return null;
    }

    private static LogTargetDto Dto(LogShipperService ship, LogTarget t) =>
        new(t.Id, t.Name, t.Kind, JsonSerializer.Deserialize<Dictionary<string, string?>>(t.SettingsJson) ?? [], [.. ship.Secrets(t).Keys], t.Enabled,
            t.LastSequence, t.LastShippedAt, t.LastError, t.LastErrorAt, t.CreatedAt);

    private static AuditEvent Event(string action, HttpContext http, LogTarget t) =>
        new(action, http.User.UserId(), "log-target", t.Id.ToString(), Data: new Dictionary<string, string> { ["name"] = t.Name, ["kind"] = t.Kind, ["enabled"] = t.Enabled.ToString() });

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
