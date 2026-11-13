using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Kb.Core.Content;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// The one <see cref="IFileStore"/> the app uses. It sends every call to the active storage place.
/// A write looks the places up in the database every time, so a switch takes effect on every server at once.
/// A read uses a copy no older than 5 seconds, and looks again when a file is not found.
/// While a move is open, a save also goes to the move's target and a delete is done in both, so nothing is missing at the switch.
/// </summary>
public sealed class ActiveFileStore(IServiceScopeFactory scopes, IEnumerable<IFileStoreKind> kinds, IDataProtectionProvider protection, TimeProvider time, IHttpContextAccessor http) : IFileStore
{
    public const string SecretPurpose = "kb.storage.secret";
    private static readonly Guid FirstPlaceId = new("5f1e7c0a-0000-4000-8000-000000000001");
    private static readonly TimeSpan ReadAge = TimeSpan.FromSeconds(5);

    private readonly IDataProtector _protector = protection.CreateProtector(SecretPurpose);
    private readonly ConcurrentDictionary<Guid, (string Stamp, IFileStore Store)> _open = new();
    private volatile Places? _places;

    private sealed record Places(DateTimeOffset At, StoragePlace Active, StoragePlace? Target, StorageMove? Move);

    public IReadOnlyList<IFileStoreKind> Kinds { get; } = [.. kinds];

    public IFileStoreKind? Kind(string? kind) => Kinds.FirstOrDefault(k => k.Kind == kind);

    /// <summary>
    /// When no place exists, the folder from configuration becomes the active one. Existing files keep working and nothing is copied.
    /// The id is fixed, so two servers starting together make one place, not two.
    /// </summary>
    public static async Task EnsureFirstPlaceAsync(KbDbContext db, string folder, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.StoragePlaces.AnyAsync(ct)) return;
        var place = new StoragePlace
        {
            Id = FirstPlaceId, Name = "Web server folder", Kind = FolderFileStoreKind.Name, Role = StorageRole.Active, CreatedAt = now,
            SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = folder }),
        };
        db.StoragePlaces.Add(place);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.Entry(place).State = EntityState.Detached; } // the other server made it first
    }

    public Dictionary<string, string> Secrets(StoragePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return place.SecretsProtected is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(place.SecretsProtected)) ?? [];
    }

    public string? Protect(IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return secrets.Count == 0 ? null : _protector.Protect(JsonSerializer.Serialize(secrets));
    }

    /// <summary>The place's settings and its secrets together, as its kind reads them.</summary>
    public Dictionary<string, string?> SettingsOf(StoragePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        var settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(place.SettingsJson) ?? [];
        foreach (var (k, v) in Secrets(place)) settings[k] = v;
        return settings;
    }

    /// <summary>The store for a place. It is kept until the place's settings change, so a network sign-in is made once.</summary>
    public IFileStore Open(StoragePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        var stamp = place.SettingsJson + "\n" + place.SecretsProtected;
        if (_open.TryGetValue(place.Id, out var kept) && kept.Stamp == stamp) return kept.Store;
        var kind = Kind(place.Kind) ?? throw new InvalidOperationException($"Storage kind '{place.Kind}' is not available.");
        var store = kind.Open(SettingsOf(place));
        _open[place.Id] = (stamp, store);
        return store;
    }

    /// <summary>Called after a change on this server, so its next read does not wait out the 5 seconds.</summary>
    public void Invalidate() => _places = null;

    private async Task<Places> PlacesAsync(bool fresh, CancellationToken ct)
    {
        var p = _places;
        if (!fresh && p is not null && time.GetUtcNow() - p.At < ReadAge) return p;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var active = await db.StoragePlaces.AsNoTracking().FirstAsync(x => x.Role == StorageRole.Active, ct);
        var move = await db.StorageMoves.AsNoTracking().FirstOrDefaultAsync(m => m.State == StorageMoveState.Copying || m.State == StorageMoveState.Copied, ct);
        var target = move is null ? null : await db.StoragePlaces.AsNoTracking().FirstOrDefaultAsync(x => x.Id == move.ToId, ct);
        return _places = new(time.GetUtcNow(), active, target, move);
    }

    /// <summary>The Admin who turned on "Read my files from" reads from the move's target. Everyone else, and every background job, reads from the active place.</summary>
    private IFileStore ReadFrom(Places p) =>
        p.Target is not null && p.Move?.PreviewUserId is { } who && http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) == who.ToString()
            ? Open(p.Target) : Open(p.Active);

    /// <summary>Does to the move's target what was just done to the active place. A failure does not undo the upload; it is counted on the move, which then cannot switch until "Copy again".</summary>
    private async Task MirrorAsync(Places p, string key, Func<IFileStore, Task> work, CancellationToken ct)
    {
        if (p.Target is null || p.Move is null) return;
        try { await work(Open(p.Target)); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var text = $"{(Guid.TryParseExact(key, "N", out var id) ? id.ToString() : key)}: {StorageRoutes.FirstLine(e.Message)}";
            var error = text[..Math.Min(text.Length, 500)];
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<KbDbContext>().StorageMoves.Where(m => m.Id == p.Move.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Failed, m => m.Failed + 1).SetProperty(m => m.LastError, error), ct);
        }
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken ct)
    {
        var p = await PlacesAsync(fresh: true, ct);
        await Open(p.Active).SaveAsync(key, data, ct);
        await MirrorAsync(p, key, t => t.SaveAsync(key, data, ct), ct);
    }

    public async Task<long> SaveAsync(string key, Stream source, long maxBytes, CancellationToken ct)
    {
        var p = await PlacesAsync(fresh: true, ct);
        var active = Open(p.Active);
        var stored = await active.SaveAsync(key, source, maxBytes, ct);
        if (stored >= 0)
        {
            await MirrorAsync(p, key, async t =>
            {
                await using var copy = await active.OpenAsync(key, ct) ?? throw new FileNotFoundException("Not found.");
                await t.SaveAsync(key, copy, long.MaxValue, ct);
            }, ct);
        }
        return stored;
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var p = await PlacesAsync(fresh: true, ct);
        await Open(p.Active).DeleteAsync(key, ct);
        await MirrorAsync(p, key, t => t.DeleteAsync(key, ct), ct);
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct) =>
        await ReadFrom(await PlacesAsync(fresh: false, ct)).OpenAsync(key, ct)
        ?? await ReadFrom(await PlacesAsync(fresh: true, ct)).OpenAsync(key, ct);

    public async Task<long?> SizeAsync(string key, CancellationToken ct) =>
        await ReadFrom(await PlacesAsync(fresh: false, ct)).SizeAsync(key, ct)
        ?? await ReadFrom(await PlacesAsync(fresh: true, ct)).SizeAsync(key, ct);
}
