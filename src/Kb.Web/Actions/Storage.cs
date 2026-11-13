// The answer of a storage place's test is also the answer of a log target's test, written by the routes in Logs/LogShipping.cs (namespace Kb.Web).
// Delete this line once no code outside Kb.Web.Actions names the record.
global using StorageTestDto = Kb.Web.Actions.StorageTestDto;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Administration > Storage. Core only: these actions are not in the contract assembly, so no plugin can hook how files are stored and moved.
// Every action here is an Admin's: AuthorizeAsync answers 403 to anyone else. An Admin is always a signed-in person, so ctx.Actor.UserId is set in HandleAsync.
// Every change is audited; no answer and no audit entry carries a secret.

public sealed record StorageKindDto(string Kind, string Label, IReadOnlyList<FileStoreField> Fields);
public sealed record StoragePlaceDto(Guid Id, string Name, string Kind, IReadOnlyDictionary<string, string?> Settings, IReadOnlyList<string> SecretsSet, StorageRole Role, DateTimeOffset CreatedAt);
public sealed record StorageMoveDto(Guid Id, Guid FromId, Guid ToId, StorageMoveState State, int Total, int Copied, int Checked, int Failed, string? LastError,
    bool Preview, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed record StorageDto(IReadOnlyList<StorageKindDto> Kinds, IReadOnlyList<StoragePlaceDto> Places, StorageMoveDto? Move);
public sealed record StorageTestDto(bool Ok, string? Error);

/// <summary>The kinds, the places and the newest move, also after it ended, so the screen can show how it went.</summary>
[Action("storage.read", Read = true)]
public sealed record ReadStorage : IAction<StorageDto>;

[Action("storage.place.add")]
public sealed record AddStoragePlace : IAction<StoragePlaceDto>
{
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }
    /// <summary>Write only: stored protected, never answered, never audited.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, string?>? Secrets { get; init; }
}

/// <summary>A place keeps its kind.</summary>
[Action("storage.place.save")]
public sealed record SaveStoragePlace : IAction<StoragePlaceDto>
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }
    /// <summary>Write only. A secret sent as null is kept; sent empty, it is cleared.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, string?>? Secrets { get; init; }
}

[Action("storage.place.remove")]
public sealed record RemoveStoragePlace : IAction<Unit>
{
    public required Guid Id { get; init; }
}

/// <summary>Saves, sizes, reads back and deletes a small file in the place.</summary>
[Action("storage.place.test")]
public sealed record TestStoragePlace : IAction<StorageTestDto>
{
    public required Guid Id { get; init; }
}

[Action("storage.move.start")]
public sealed record StartStorageMove : IAction<StorageMoveDto>
{
    public required Guid ToId { get; init; }
}

/// <summary>Copies only what is missing or differs: a file already there with the same size is skipped.</summary>
[Action("storage.move.copy-again")]
public sealed record CopyStorageMoveAgain : IAction<Unit>;

/// <summary>The caller alone reads from the new place, to try it before the switch.</summary>
[Action("storage.move.preview")]
public sealed record PreviewStorageMove : IAction<Unit>
{
    public required bool On { get; init; }
}

[Action("storage.move.switch")]
public sealed record SwitchStorageMove : IAction<Unit>;

/// <summary>Files already copied stay where they are.</summary>
[Action("storage.move.cancel")]
public sealed record CancelStorageMove : IAction<Unit>;

internal sealed class ReadStorageHandler(KbDbContext db, ActiveFileStore store, IAccess access) : IActionHandler<ReadStorage, StorageDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadStorage> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<StorageDto>> HandleAsync(ActionContext<ReadStorage> ctx)
    {
        var places = await db.StoragePlaces.AsNoTracking().ToListAsync(ctx.Cancel);
        var move = (await db.StorageMoves.AsNoTracking().ToListAsync(ctx.Cancel)).MaxBy(m => m.StartedAt);
        return new StorageDto(
            [.. store.Kinds.Select(k => new StorageKindDto(k.Kind, k.Label, k.Fields))],
            [.. places.OrderBy(p => p.CreatedAt).Select(p => StorageSupport.Dto(store, p))],
            move is null ? null : StorageSupport.Dto(move, ctx.Actor.UserId));
    }
}

internal sealed class AddStoragePlaceHandler(KbDbContext db, ActiveFileStore store, IAccess access, TimeProvider time) : IActionHandler<AddStoragePlace, StoragePlaceDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<AddStoragePlace> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<StoragePlaceDto>> HandleAsync(ActionContext<AddStoragePlace> ctx)
    {
        var req = ctx.Action;
        if (store.Kind(req.Kind) is not { } kind) return Problem.Invalid("kind", "Unknown kind.");
        var place = new StoragePlace { Name = "", Kind = kind.Kind, Role = StorageRole.Old, CreatedAt = time.GetUtcNow() };
        if (StorageSupport.Apply(store, kind, place, req.Name, req.Settings, req.Secrets) is { } invalid) return invalid;
        db.StoragePlaces.Add(place);
        await db.SaveChangesAsync();
        StorageSupport.Audit(ctx, AuditActions.StoragePlaceAdd, place);
        return StorageSupport.Dto(store, place);
    }
}

internal sealed class SaveStoragePlaceHandler(KbDbContext db, ActiveFileStore store, IAccess access) : IActionHandler<SaveStoragePlace, StoragePlaceDto>
{
    private StoragePlace? _place;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveStoragePlace> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.Id;
        return (_place = await db.StoragePlaces.FirstOrDefaultAsync(p => p.Id == id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<StoragePlaceDto>> HandleAsync(ActionContext<SaveStoragePlace> ctx)
    {
        var (req, place) = (ctx.Action, _place!);
        if (store.Kind(place.Kind) is not { } kind) return Problem.Invalid("kind", "Unknown kind.");
        if (StorageSupport.Apply(store, kind, place, req.Name, req.Settings, req.Secrets) is { } invalid) return invalid;
        await db.SaveChangesAsync();
        store.Invalidate();
        StorageSupport.Audit(ctx, AuditActions.StoragePlaceChange, place);
        return StorageSupport.Dto(store, place);
    }
}

internal sealed class TestStoragePlaceHandler(KbDbContext db, ActiveFileStore store, IAccess access) : IActionHandler<TestStoragePlace, StorageTestDto>
{
    private StoragePlace? _place;

    public async Task<Problem?> AuthorizeAsync(ActionContext<TestStoragePlace> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.Id;
        return (_place = await db.StoragePlaces.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<StorageTestDto>> HandleAsync(ActionContext<TestStoragePlace> ctx)
    {
        var error = await StorageSupport.TestAsync(store, _place!, ctx.Cancel);
        return new StorageTestDto(error is null, error);
    }
}

internal sealed class RemoveStoragePlaceHandler(KbDbContext db, IAccess access) : IActionHandler<RemoveStoragePlace, Unit>
{
    private StoragePlace? _place;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveStoragePlace> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.Id;
        return (_place = await db.StoragePlaces.FirstOrDefaultAsync(p => p.Id == id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveStoragePlace> ctx)
    {
        var place = _place!;
        if (place.Role != StorageRole.Old) return StorageSupport.Refused("In use.");
        db.StoragePlaces.Remove(place);
        await db.SaveChangesAsync();
        StorageSupport.Audit(ctx, AuditActions.StoragePlaceRemove, place);
        return new Unit();
    }
}

internal sealed class StartStorageMoveHandler(KbDbContext db, ActiveFileStore store, StorageMover mover, IAccess access, TimeProvider time) : IActionHandler<StartStorageMove, StorageMoveDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<StartStorageMove> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<StorageMoveDto>> HandleAsync(ActionContext<StartStorageMove> ctx)
    {
        var toId = ctx.Action.ToId;
        if (await StorageSupport.OpenMoveAsync(db, ctx.Cancel) is not null) return StorageSupport.Refused("A move is already running.");
        var to = await db.StoragePlaces.FirstOrDefaultAsync(p => p.Id == toId && p.Role == StorageRole.Old, ctx.Cancel);
        if (to is null) return Problem.NotFound;
        if (await StorageSupport.TestAsync(store, to, ctx.Cancel) is { } error) return StorageSupport.Refused(error);
        var from = await db.StoragePlaces.FirstAsync(p => p.Role == StorageRole.Active, ctx.Cancel);
        var move = new StorageMove { FromId = from.Id, ToId = to.Id, State = StorageMoveState.Copying, StartedAt = time.GetUtcNow() };
        to.Role = StorageRole.Target;
        db.StorageMoves.Add(move);
        await db.SaveChangesAsync();
        store.Invalidate();
        StorageSupport.Audit(ctx, AuditActions.StorageMoveStart, move, from, to);
        mover.Wake();
        return StorageSupport.Dto(move, ctx.Actor.UserId);
    }
}

internal sealed class CopyStorageMoveAgainHandler(KbDbContext db, StorageMover mover, IAccess access) : IActionHandler<CopyStorageMoveAgain, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CopyStorageMoveAgain> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<CopyStorageMoveAgain> ctx)
    {
        if (await StorageSupport.OpenMoveAsync(db, ctx.Cancel) is not { } move) return StorageSupport.Refused("No move is running.");
        if (move.State == StorageMoveState.Copied)
        {
            (move.State, move.Copied, move.Checked, move.Failed, move.LastError, move.ClaimedBy, move.ClaimedUntil) = (StorageMoveState.Copying, 0, 0, 0, null, null, null);
            await db.SaveChangesAsync();
            mover.Wake();
        }
        return new Unit();
    }
}

internal sealed class PreviewStorageMoveHandler(KbDbContext db, ActiveFileStore store, IAccess access) : IActionHandler<PreviewStorageMove, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<PreviewStorageMove> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<PreviewStorageMove> ctx)
    {
        if (await StorageSupport.OpenMoveAsync(db, ctx.Cancel) is not { State: StorageMoveState.Copied } move) return StorageSupport.Refused("Not everything was copied.");
        var me = ctx.Actor.UserId;
        if (ctx.Action.On) move.PreviewUserId = me;
        else if (move.PreviewUserId == me) move.PreviewUserId = null;
        await db.SaveChangesAsync();
        store.Invalidate();
        return new Unit();
    }
}

internal sealed class SwitchStorageMoveHandler(KbDbContext db, ActiveFileStore store, IAccess access, TimeProvider time) : IActionHandler<SwitchStorageMove, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SwitchStorageMove> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    // One save: the two roles and the move's state change together. No page, image or attachment row is touched.
    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SwitchStorageMove> ctx)
    {
        if (await StorageSupport.OpenMoveAsync(db, ctx.Cancel) is not { State: StorageMoveState.Copied, Failed: 0 } move) return StorageSupport.Refused("Not everything was copied.");
        var from = await db.StoragePlaces.FirstAsync(p => p.Id == move.FromId, ctx.Cancel);
        var to = await db.StoragePlaces.FirstAsync(p => p.Id == move.ToId, ctx.Cancel);
        (from.Role, to.Role) = (StorageRole.Old, StorageRole.Active);
        (move.State, move.FinishedAt, move.PreviewUserId) = (StorageMoveState.Switched, time.GetUtcNow(), null);
        await db.SaveChangesAsync();
        store.Invalidate();
        StorageSupport.Audit(ctx, AuditActions.StorageMoveSwitch, move, from, to);
        return new Unit();
    }
}

internal sealed class CancelStorageMoveHandler(KbDbContext db, ActiveFileStore store, IAccess access, TimeProvider time) : IActionHandler<CancelStorageMove, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CancelStorageMove> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<CancelStorageMove> ctx)
    {
        if (await StorageSupport.OpenMoveAsync(db, ctx.Cancel) is not { } move) return StorageSupport.Refused("No move is running.");
        var from = await db.StoragePlaces.FirstAsync(p => p.Id == move.FromId, ctx.Cancel);
        var to = await db.StoragePlaces.FirstAsync(p => p.Id == move.ToId, ctx.Cancel);
        to.Role = StorageRole.Old;
        (move.State, move.FinishedAt, move.PreviewUserId) = (StorageMoveState.Cancelled, time.GetUtcNow(), null);
        await db.SaveChangesAsync();
        store.Invalidate();
        StorageSupport.Audit(ctx, AuditActions.StorageMoveCancel, move, from, to);
        return new Unit();
    }
}

/// <summary>What the storage handlers share.</summary>
internal static class StorageSupport
{
    public static Task<StorageMove?> OpenMoveAsync(KbDbContext db, CancellationToken ct) =>
        db.StorageMoves.FirstOrDefaultAsync(m => m.State == StorageMoveState.Copying || m.State == StorageMoveState.Copied, ct);

    /// <summary>Saves, sizes, reads back and deletes a small file. Null when it all worked; otherwise the first line of the system's error.</summary>
    public static async Task<string?> TestAsync(ActiveFileStore store, StoragePlace place, CancellationToken ct)
    {
        var key = "_test-" + Guid.NewGuid().ToString("N");
        var data = "storage test"u8.ToArray();
        try
        {
            var s = store.Open(place);
            await s.SaveAsync(key, data, ct);
            try
            {
                if (await s.SizeAsync(key, ct) != data.Length) return "The stored size differs.";
                await using var read = await s.OpenAsync(key, ct);
                if (read is null) return "The file was not found after saving.";
                using var ms = new MemoryStream();
                await read.CopyToAsync(ms, ct);
                if (!ms.ToArray().AsSpan().SequenceEqual(data)) return "The stored content differs.";
            }
            finally { await s.DeleteAsync(key, ct); }
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return StorageRoutes.FirstLine(e.Message); }
    }

    /// <summary>Puts the name, settings and secrets on the place. A secret sent as null is kept; sent empty, it is cleared.</summary>
    public static Problem? Apply(ActiveFileStore store, IFileStoreKind kind, StoragePlace place, string? name,
        IReadOnlyDictionary<string, string?>? newSettings, IReadOnlyDictionary<string, string?>? newSecrets)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Problem.Invalid("name", "1 to 100 characters.");
        var settings = new Dictionary<string, string?>();
        var secrets = store.Secrets(place);
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
        place.Name = name;
        place.SettingsJson = JsonSerializer.Serialize(settings);
        place.SecretsProtected = store.Protect(secrets);
        return null;
    }

    public static StoragePlaceDto Dto(ActiveFileStore store, StoragePlace p) =>
        new(p.Id, p.Name, p.Kind, JsonSerializer.Deserialize<Dictionary<string, string?>>(p.SettingsJson) ?? [], [.. store.Secrets(p).Keys], p.Role, p.CreatedAt);

    public static StorageMoveDto Dto(StorageMove m, Guid? me) =>
        new(m.Id, m.FromId, m.ToId, m.State, m.Total, m.Copied, m.Checked, m.Failed, m.LastError, me is not null && m.PreviewUserId == me, m.StartedAt, m.FinishedAt);

    public static void Audit(ActionContext ctx, string action, StoragePlace p) =>
        ctx.Audit(action, "storage-place", p.Id.ToString(), data: new Dictionary<string, string> { ["name"] = p.Name, ["kind"] = p.Kind });

    public static void Audit(ActionContext ctx, string action, StorageMove m, StoragePlace from, StoragePlace to)
    {
        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        ctx.Audit(action, "storage-move", m.Id.ToString(), data: new Dictionary<string, string>
        {
            ["from"] = from.Name, ["to"] = to.Name, ["total"] = N(m.Total), ["copied"] = N(m.Copied), ["checked"] = N(m.Checked), ["failed"] = N(m.Failed),
        });
    }

    public static Problem Refused(string error) => Problem.Conflict(body: new { error });
}
