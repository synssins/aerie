using System.Text.Json;
using System.Text.Json.Serialization;
using Kb.Core.Audit;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Administration > Log shipping. Core only: these actions are not in the contract assembly, so no plugin can hook how audit entries leave the server.
// Every action here is an Admin's: AuthorizeAsync answers 403 to anyone else. Every change is audited; no answer and no audit entry carries a secret.

public sealed record LogKindDto(string Kind, string Label, IReadOnlyList<LogShipField> Fields);
public sealed record LogTargetDto(Guid Id, string Name, string Kind, IReadOnlyDictionary<string, string?> Settings, IReadOnlyList<string> SecretsSet, bool Enabled,
    long LastSequence, DateTimeOffset? LastShippedAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset CreatedAt);
/// <summary>Head is the newest audit entry, so the screen can show how far behind a target is.</summary>
public sealed record LogsDto(IReadOnlyList<LogKindDto> Kinds, IReadOnlyList<LogTargetDto> Targets, long Head);

[Action("log.read", Read = true)]
public sealed record ReadLogs : IAction<LogsDto>;

/// <summary>A new target starts at the newest entry: history is not sent to it.</summary>
[Action("log.target.add")]
public sealed record AddLogTarget : IAction<LogTargetDto>
{
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }
    /// <summary>Write only: stored protected, never answered, never audited.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, string?>? Secrets { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>The kind of a target does not change. A secret sent as null is kept; sent empty, it is cleared.</summary>
[Action("log.target.save")]
public sealed record SaveLogTarget : IAction<LogTargetDto>
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }
    /// <summary>Write only: stored protected, never answered, never audited.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, string?>? Secrets { get; init; }
    public bool Enabled { get; init; } = true;
}

[Action("log.target.remove")]
public sealed record RemoveLogTarget : IAction<Unit>
{
    public required Guid Id { get; init; }
}

/// <summary>Sends one entry named "logs.test" to the target. It is not part of the audit log.</summary>
[Action("log.target.test")]
public sealed record TestLogTarget : IAction<StorageTestDto>
{
    public required Guid Id { get; init; }
}

internal static class LogTargets
{
    /// <summary>Puts the request on the target. A secret sent as null is kept; sent empty, it is cleared.</summary>
    public static Problem? Apply(LogShipperService ship, ILogShipperKind kind, LogTarget target, string? name,
        IReadOnlyDictionary<string, string?>? newSettings, IReadOnlyDictionary<string, string?>? newSecrets, bool enabled)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Problem.Invalid("name", "1 to 100 characters.");
        var settings = new Dictionary<string, string?>();
        var secrets = ship.Secrets(target);
        foreach (var f in kind.Fields)
        {
            if (f.Type != "secret")
            {
                if (newSettings?.GetValueOrDefault(f.Key)?.Trim() is { Length: > 0 } v) settings[f.Key] = v;
            }
            else if (newSecrets?.GetValueOrDefault(f.Key) is { } secret)
            {
                if (secret.Length == 0) secrets.Remove(f.Key);
                else secrets[f.Key] = secret;
            }
        }
        try { kind.Open(settings.Concat(secrets.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).ToDictionary()); }
        catch (SettingException e) { return Problem.Invalid(e.Field, e.Message); }
        target.Name = name;
        target.Enabled = enabled;
        target.SettingsJson = JsonSerializer.Serialize(settings);
        target.SecretsProtected = ship.Protect(secrets);
        return null;
    }

    public static LogTargetDto Dto(LogShipperService ship, LogTarget t) =>
        new(t.Id, t.Name, t.Kind, JsonSerializer.Deserialize<Dictionary<string, string?>>(t.SettingsJson) ?? [], [.. ship.Secrets(t).Keys], t.Enabled,
            t.LastSequence, t.LastShippedAt, t.LastError, t.LastErrorAt, t.CreatedAt);

    /// <summary>What a change to a target leaves in the audit log: its name, kind and state, never a setting or a secret.</summary>
    public static void Audit(ActionContext ctx, string action, LogTarget t) =>
        ctx.Audit(action, "log-target", t.Id.ToString(), data: new Dictionary<string, string> { ["name"] = t.Name, ["kind"] = t.Kind, ["enabled"] = t.Enabled.ToString() });
}

internal sealed class ReadLogsHandler(KbDbContext db, LogShipperService ship, IAccess access) : IActionHandler<ReadLogs, LogsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadLogs> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<LogsDto>> HandleAsync(ActionContext<ReadLogs> ctx)
    {
        var targets = await db.LogTargets.AsNoTracking().ToListAsync(ctx.Cancel);
        var head = await db.AuditEntries.MaxAsync(a => (long?)a.Sequence, ctx.Cancel) ?? 0;
        return new LogsDto([.. ship.Kinds.Select(k => new LogKindDto(k.Kind, k.Label, k.Fields))], [.. targets.OrderBy(t => t.CreatedAt).Select(t => LogTargets.Dto(ship, t))], head);
    }
}

internal sealed class AddLogTargetHandler(KbDbContext db, LogShipperService ship, IAccess access, TimeProvider time) : IActionHandler<AddLogTarget, LogTargetDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<AddLogTarget> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<LogTargetDto>> HandleAsync(ActionContext<AddLogTarget> ctx)
    {
        var req = ctx.Action;
        if (ship.Kind(req.Kind) is not { } kind) return Problem.Invalid("kind", "Unknown kind.");
        var target = new LogTarget { Name = "", Kind = kind.Kind, CreatedAt = time.GetUtcNow(), LastSequence = await db.AuditEntries.MaxAsync(a => (long?)a.Sequence, ctx.Cancel) ?? 0 };
        if (LogTargets.Apply(ship, kind, target, req.Name, req.Settings, req.Secrets, req.Enabled) is { } invalid) return invalid;
        db.LogTargets.Add(target);
        await db.SaveChangesAsync();
        LogTargets.Audit(ctx, AuditActions.LogTargetAdd, target);
        return LogTargets.Dto(ship, target);
    }
}

internal sealed class SaveLogTargetHandler(KbDbContext db, LogShipperService ship, IAccess access) : IActionHandler<SaveLogTarget, LogTargetDto>
{
    private LogTarget? _target;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveLogTarget> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_target = await db.LogTargets.FirstOrDefaultAsync(t => t.Id == ctx.Action.Id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<LogTargetDto>> HandleAsync(ActionContext<SaveLogTarget> ctx)
    {
        var (req, target) = (ctx.Action, _target!);
        if (ship.Kind(target.Kind) is not { } kind) return Problem.Invalid("kind", "Unknown kind.");
        if (LogTargets.Apply(ship, kind, target, req.Name, req.Settings, req.Secrets, req.Enabled) is { } invalid) return invalid;
        await db.SaveChangesAsync();
        LogTargets.Audit(ctx, AuditActions.LogTargetChange, target);
        return LogTargets.Dto(ship, target);
    }
}

internal sealed class RemoveLogTargetHandler(KbDbContext db, IAccess access) : IActionHandler<RemoveLogTarget, Unit>
{
    private LogTarget? _target;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveLogTarget> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_target = await db.LogTargets.FirstOrDefaultAsync(t => t.Id == ctx.Action.Id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveLogTarget> ctx)
    {
        db.LogTargets.Remove(_target!);
        await db.SaveChangesAsync();
        LogTargets.Audit(ctx, AuditActions.LogTargetRemove, _target!);
        return new Unit();
    }
}

internal sealed class TestLogTargetHandler(KbDbContext db, LogShipperService ship, IAccess access, TimeProvider time) : IActionHandler<TestLogTarget, StorageTestDto>
{
    private LogTarget? _target;

    public async Task<Problem?> AuthorizeAsync(ActionContext<TestLogTarget> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_target = await db.LogTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ctx.Action.Id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<StorageTestDto>> HandleAsync(ActionContext<TestLogTarget> ctx)
    {
        var me = ctx.Actor.UserId;
        var name = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.UserName).FirstOrDefaultAsync(ctx.Cancel);
        try
        {
            await ship.Open(_target!).SendAsync([new LogEvent { Sequence = 0, At = time.GetUtcNow(), Action = "logs.test", ActorId = me, Actor = name, Server = Environment.MachineName }], ctx.Cancel);
            return new StorageTestDto(true, null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new StorageTestDto(false, StorageRoutes.FirstLine(e.Message)); }
    }
}
