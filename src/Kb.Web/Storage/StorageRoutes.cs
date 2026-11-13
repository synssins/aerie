using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

public sealed record StorageKindDto(string Kind, string Label, IReadOnlyList<FileStoreField> Fields);
public sealed record StoragePlaceDto(Guid Id, string Name, string Kind, IReadOnlyDictionary<string, string?> Settings, IReadOnlyList<string> SecretsSet, StorageRole Role, DateTimeOffset CreatedAt);
public sealed record StorageMoveDto(Guid Id, Guid FromId, Guid ToId, StorageMoveState State, int Total, int Copied, int Checked, int Failed, string? LastError,
    bool Preview, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed record StorageDto(IReadOnlyList<StorageKindDto> Kinds, IReadOnlyList<StoragePlaceDto> Places, StorageMoveDto? Move);
public sealed record StorageMoveRequest(Guid ToId);
public sealed record StoragePreviewRequest(bool On);
public sealed record StoragePlaceRequest(string? Name, string? Kind, Dictionary<string, string?>? Settings, Dictionary<string, string?>? Secrets);
public sealed record StorageTestDto(bool Ok, string? Error);

/// <summary>Administration > Storage. Admins only; every change is audited; no answer carries a secret.</summary>
public static class StorageRoutes
{
    public static void MapStorageEndpoints(this RouteGroupBuilder api)
    {
        var g = api.AdminGroup().MapGroup("/storage");

        // "move" is the newest move, also after it ended, so the screen can show how it went.
        g.MapGet("/", async (HttpContext http, KbDbContext db, ActiveFileStore store, CancellationToken ct) =>
        {
            var places = await db.StoragePlaces.AsNoTracking().ToListAsync(ct);
            var move = (await db.StorageMoves.AsNoTracking().ToListAsync(ct)).MaxBy(m => m.StartedAt);
            return Results.Ok(new StorageDto(
                [.. store.Kinds.Select(k => new StorageKindDto(k.Kind, k.Label, k.Fields))],
                [.. places.OrderBy(p => p.CreatedAt).Select(p => Dto(store, p))],
                move is null ? null : Dto(move, http.User.UserId())));
        });

        g.MapPost("/places", async (StoragePlaceRequest req, HttpContext http, KbDbContext db, ActiveFileStore store, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (store.Kind(req.Kind) is not { } kind) return Invalid("kind", "Unknown kind.");
            var place = new StoragePlace { Name = "", Kind = kind.Kind, Role = StorageRole.Old, CreatedAt = time.GetUtcNow() };
            if (Apply(store, kind, place, req) is { } invalid) return invalid;
            db.StoragePlaces.Add(place);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(Event(AuditActions.StoragePlaceAdd, http, place), ct);
            return Results.Created($"/api/v1/admin/storage/places/{place.Id}", Dto(store, place));
        });

        g.MapPut("/places/{id:guid}", async (Guid id, StoragePlaceRequest req, HttpContext http, KbDbContext db, ActiveFileStore store, IAuditLog audit, CancellationToken ct) =>
        {
            var place = await db.StoragePlaces.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (place is null) return Results.NotFound();
            if (store.Kind(place.Kind) is not { } kind) return Invalid("kind", "Unknown kind.");
            if (Apply(store, kind, place, req) is { } invalid) return invalid;
            await db.SaveChangesAsync(ct);
            store.Invalidate();
            await audit.AppendAsync(Event(AuditActions.StoragePlaceChange, http, place), ct);
            return Results.Ok(Dto(store, place));
        });

        g.MapPost("/places/{id:guid}/test", async (Guid id, KbDbContext db, ActiveFileStore store, CancellationToken ct) =>
        {
            var place = await db.StoragePlaces.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (place is null) return Results.NotFound();
            var error = await TestAsync(store, place, ct);
            return Results.Ok(new StorageTestDto(error is null, error));
        });

        g.MapDelete("/places/{id:guid}", async (Guid id, HttpContext http, KbDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var place = await db.StoragePlaces.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (place is null) return Results.NotFound();
            if (place.Role != StorageRole.Old) return Refused("In use.");
            db.StoragePlaces.Remove(place);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(Event(AuditActions.StoragePlaceRemove, http, place), ct);
            return Results.NoContent();
        });

        g.MapPost("/move", async (StorageMoveRequest req, HttpContext http, KbDbContext db, ActiveFileStore store, StorageMover mover, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (await OpenMoveAsync(db, ct) is not null) return Refused("A move is already running.");
            var to = await db.StoragePlaces.FirstOrDefaultAsync(p => p.Id == req.ToId && p.Role == StorageRole.Old, ct);
            if (to is null) return Results.NotFound();
            if (await TestAsync(store, to, ct) is { } error) return Refused(error);
            var from = await db.StoragePlaces.FirstAsync(p => p.Role == StorageRole.Active, ct);
            var move = new StorageMove { FromId = from.Id, ToId = to.Id, State = StorageMoveState.Copying, StartedAt = time.GetUtcNow() };
            to.Role = StorageRole.Target;
            db.StorageMoves.Add(move);
            await db.SaveChangesAsync(ct);
            store.Invalidate();
            await audit.AppendAsync(Event(AuditActions.StorageMoveStart, http, move, from, to), ct);
            mover.Wake();
            return Results.Created("/api/v1/admin/storage", Dto(move, http.User.UserId()));
        });

        // Copies only what is missing or differs: a file already there with the same size is skipped.
        g.MapPost("/move/copy-again", async (KbDbContext db, StorageMover mover, CancellationToken ct) =>
        {
            if (await OpenMoveAsync(db, ct) is not { } move) return Refused("No move is running.");
            if (move.State == StorageMoveState.Copied)
            {
                (move.State, move.Copied, move.Checked, move.Failed, move.LastError, move.ClaimedBy, move.ClaimedUntil) = (StorageMoveState.Copying, 0, 0, 0, null, null, null);
                await db.SaveChangesAsync(ct);
                mover.Wake();
            }
            return Results.NoContent();
        });

        g.MapPut("/move/preview", async (StoragePreviewRequest req, HttpContext http, KbDbContext db, ActiveFileStore store, CancellationToken ct) =>
        {
            if (await OpenMoveAsync(db, ct) is not { State: StorageMoveState.Copied } move) return Refused("Not everything was copied.");
            var me = http.User.UserId();
            if (req.On) move.PreviewUserId = me;
            else if (move.PreviewUserId == me) move.PreviewUserId = null;
            await db.SaveChangesAsync(ct);
            store.Invalidate();
            return Results.NoContent();
        });

        // One save: the two roles and the move's state change together. No page, image or attachment row is touched.
        g.MapPost("/move/switch", async (HttpContext http, KbDbContext db, ActiveFileStore store, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (await OpenMoveAsync(db, ct) is not { State: StorageMoveState.Copied, Failed: 0 } move) return Refused("Not everything was copied.");
            var from = await db.StoragePlaces.FirstAsync(p => p.Id == move.FromId, ct);
            var to = await db.StoragePlaces.FirstAsync(p => p.Id == move.ToId, ct);
            (from.Role, to.Role) = (StorageRole.Old, StorageRole.Active);
            (move.State, move.FinishedAt, move.PreviewUserId) = (StorageMoveState.Switched, time.GetUtcNow(), null);
            await db.SaveChangesAsync(ct);
            store.Invalidate();
            await audit.AppendAsync(Event(AuditActions.StorageMoveSwitch, http, move, from, to), ct);
            return Results.NoContent();
        });

        // Files already copied stay where they are.
        g.MapPost("/move/cancel", async (HttpContext http, KbDbContext db, ActiveFileStore store, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (await OpenMoveAsync(db, ct) is not { } move) return Refused("No move is running.");
            var from = await db.StoragePlaces.FirstAsync(p => p.Id == move.FromId, ct);
            var to = await db.StoragePlaces.FirstAsync(p => p.Id == move.ToId, ct);
            to.Role = StorageRole.Old;
            (move.State, move.FinishedAt, move.PreviewUserId) = (StorageMoveState.Cancelled, time.GetUtcNow(), null);
            await db.SaveChangesAsync(ct);
            store.Invalidate();
            await audit.AppendAsync(Event(AuditActions.StorageMoveCancel, http, move, from, to), ct);
            return Results.NoContent();
        });
    }

    private static Task<StorageMove?> OpenMoveAsync(KbDbContext db, CancellationToken ct) =>
        db.StorageMoves.FirstOrDefaultAsync(m => m.State == StorageMoveState.Copying || m.State == StorageMoveState.Copied, ct);

    /// <summary>Saves, sizes, reads back and deletes a small file. Null when it all worked; otherwise the first line of the system's error.</summary>
    internal static async Task<string?> TestAsync(ActiveFileStore store, StoragePlace place, CancellationToken ct)
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
        catch (Exception e) when (e is not OperationCanceledException) { return FirstLine(e.Message); }
    }

    internal static string FirstLine(string message) => message.Split('\r', '\n')[0];

    /// <summary>Puts the request's name, settings and secrets on the place. A secret sent as null is kept; sent empty, it is cleared.</summary>
    private static IResult? Apply(ActiveFileStore store, IFileStoreKind kind, StoragePlace place, StoragePlaceRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Invalid("name", "1 to 100 characters.");
        var settings = new Dictionary<string, string?>();
        var secrets = store.Secrets(place);
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
        catch (FileStoreSettingException e) { return Invalid(e.Field, e.Message); }
        place.Name = name;
        place.SettingsJson = JsonSerializer.Serialize(settings);
        place.SecretsProtected = store.Protect(secrets);
        return null;
    }

    private static StoragePlaceDto Dto(ActiveFileStore store, StoragePlace p) =>
        new(p.Id, p.Name, p.Kind, JsonSerializer.Deserialize<Dictionary<string, string?>>(p.SettingsJson) ?? [], [.. store.Secrets(p).Keys], p.Role, p.CreatedAt);

    private static AuditEvent Event(string action, HttpContext http, StoragePlace p) =>
        new(action, http.User.UserId(), "storage-place", p.Id.ToString(), Data: new Dictionary<string, string> { ["name"] = p.Name, ["kind"] = p.Kind });

    private static StorageMoveDto Dto(StorageMove m, Guid me) =>
        new(m.Id, m.FromId, m.ToId, m.State, m.Total, m.Copied, m.Checked, m.Failed, m.LastError, m.PreviewUserId == me, m.StartedAt, m.FinishedAt);

    private static AuditEvent Event(string action, HttpContext http, StorageMove m, StoragePlace from, StoragePlace to)
    {
        static string N(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(action, http.User.UserId(), "storage-move", m.Id.ToString(), Data: new Dictionary<string, string>
        {
            ["from"] = from.Name, ["to"] = to.Name, ["total"] = N(m.Total), ["copied"] = N(m.Copied), ["checked"] = N(m.Checked), ["failed"] = N(m.Failed),
        });
    }

    private static IResult Refused(string error) => Results.Conflict(new { error });

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
