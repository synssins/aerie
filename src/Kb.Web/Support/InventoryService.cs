using System.Text;
using Kb.Core.Inventory;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// Who may view and edit the application inventory, and the one update every bulk change goes through (CSV import, sync from a
/// plugin or a script): idempotent, recorded in each install's history.
/// </summary>
public sealed class InventoryService(KbDbContext db, IAccess access, TimeProvider time)
{
    public const int MaxRows = 50_000;

    public async Task<bool> CanViewAsync(Actor actor, CancellationToken ct) => await AllowedAsync(actor, edit: false, ct);
    public async Task<bool> CanEditAsync(Actor actor, CancellationToken ct) => await AllowedAsync(actor, edit: true, ct);

    private async Task<bool> AllowedAsync(Actor actor, bool edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        // A plugin's own work (a CMDB feed) keeps the inventory up to date; it is installed and enabled by an Admin.
        if (actor.Kind == ActorKind.Plugin) return true;
        if (actor.UserId is not { } me) return false;
        if (await access.IsAdminAsync(actor, ct)) return true;
        // Editing includes viewing.
        var rules = await db.InvAccessRules.AsNoTracking().Where(r => r.Edit || !edit).ToListAsync(ct);
        if (rules.Count == 0) return false;
        if (rules.Any(r => r.Kind == InvRuleKind.Everyone)) return true;
        if (actor.TokenId is { } token && rules.Any(r => r.Kind == InvRuleKind.Token && string.Equals(r.TargetId, token.ToString("D"), StringComparison.OrdinalIgnoreCase))) return true;
        var meText = me.ToString("D");
        if (rules.Any(r => r.Kind == InvRuleKind.User && string.Equals(r.TargetId, meText, StringComparison.OrdinalIgnoreCase))) return true;
        var groups = await db.KbGroupMembers.Where(m => m.UserId == me).Join(db.KbGroups, m => m.GroupId, g => g.Id, (m, g) => g.Key).ToListAsync(ct);
        if (rules.Any(r => r.Kind == InvRuleKind.Group && groups.Contains(r.TargetId ?? "", StringComparer.OrdinalIgnoreCase))) return true;
        foreach (var r in rules.Where(r => r.Kind == InvRuleKind.Space))
            if (Guid.TryParse(r.TargetId, out var space) && await access.RoleAsync(actor, space, ct) is not null) return true;
        var roleIds = rules.Where(r => r.Kind == InvRuleKind.Role && Guid.TryParse(r.TargetId, out _)).Select(r => Guid.Parse(r.TargetId!)).ToList();
        if (roleIds.Count == 0) return false;
        var principals = groups.Append(meText).ToList();
        return await db.SpaceRoleAssignments.AnyAsync(a => roleIds.Contains(a.RoleId) && principals.Contains(a.PrincipalId), ct);
    }

    public static string Key(string text) => text.Trim().ToLowerInvariant();

    /// <summary>Checks the rows, then adds, changes and (removeMissing) removes installs. Nothing is stored unless apply.</summary>
    public async Task<Outcome<SyncResultDto>> SyncAsync(IReadOnlyList<SyncRowDto> rows, bool removeMissing, bool apply, string by, CancellationToken ct)
    {
        if (rows.Count > MaxRows) return Problem.Invalid("rows", $"At most {MaxRows} rows.");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (string.IsNullOrWhiteSpace(r.Application) || string.IsNullOrWhiteSpace(r.Server) || string.IsNullOrWhiteSpace(r.Version))
                return Problem.Invalid("rows", $"Row {i + 1}: application, server and version are required.");
            if (r.Application.Trim().Length > 200 || r.Server.Trim().Length > 200 || r.Version.Trim().Length > 100 || (r.Vendor?.Trim().Length ?? 0) > 200)
                return Problem.Invalid("rows", $"Row {i + 1}: too long.");
        }
        // The last row for an application and server wins.
        var wanted = rows.GroupBy(r => (App: Key(r.Application), Server: Key(r.Server))).Select(g => g.Last()).ToList();
        var appKeys = wanted.Select(r => Key(r.Application)).Distinct().ToList();
        var apps = await db.InvApplications.Where(a => appKeys.Contains(a.NameKey)).ToDictionaryAsync(a => a.NameKey, ct);
        var serverKeys = wanted.Select(r => Key(r.Server)).Distinct().ToList();
        var appIds = apps.Values.Select(a => a.Id).ToList();
        var installs = await db.InvInstalls.Where(i => appIds.Contains(i.ApplicationId) || (removeMissing && serverKeys.Contains(i.ServerKey))).ToListAsync(ct);
        var now = time.GetUtcNow();
        var changes = new List<SyncChangeDto>();
        int added = 0, changed = 0, unchanged = 0, removed = 0, newApps = 0;

        foreach (var row in wanted)
        {
            var (appKey, serverKey) = (Key(row.Application), Key(row.Server));
            var (version, server, vendor) = (row.Version.Trim(), row.Server.Trim(), string.IsNullOrWhiteSpace(row.Vendor) ? null : row.Vendor.Trim());
            if (!apps.TryGetValue(appKey, out var app))
            {
                app = new InvApplication { Name = row.Application.Trim(), NameKey = appKey, Vendor = vendor, CreatedAt = now, UpdatedAt = now };
                apps[appKey] = app;
                newApps++;
                if (apply) db.InvApplications.Add(app);
            }
            else if (app.Vendor is null && vendor is not null && apply) { app.Vendor = vendor; app.UpdatedAt = now; }
            var install = installs.FirstOrDefault(i => i.ApplicationId == app.Id && i.ServerKey == serverKey);
            if (install is null)
            {
                added++;
                changes.Add(new(app.Name, server, "Added", null, version));
                if (!apply) continue;
                db.InvInstalls.Add(new InvInstall { ApplicationId = app.Id, Server = server, ServerKey = serverKey, Version = version, ChangedAt = now, ChangedBy = by });
                db.InvHistory.Add(new InvHistory { ApplicationId = app.Id, Server = server, ServerKey = serverKey, Kind = InvChange.Added, To = version, At = now, By = by });
            }
            else if (install.Version != version)
            {
                changed++;
                changes.Add(new(app.Name, install.Server, "Changed", install.Version, version));
                if (!apply) continue;
                db.InvHistory.Add(new InvHistory { ApplicationId = app.Id, Server = install.Server, ServerKey = serverKey, Kind = InvChange.Changed, From = install.Version, To = version, At = now, By = by });
                (install.Version, install.ChangedAt, install.ChangedBy) = (version, now, by);
            }
            else unchanged++;
        }

        if (removeMissing)
        {
            // Only the servers the list names: a list for two servers says nothing about the rest.
            var keep = wanted.Select(r => (App: apps[Key(r.Application)].Id, Server: Key(r.Server))).ToHashSet();
            var names = await db.InvApplications.AsNoTracking().Where(a => installs.Select(i => i.ApplicationId).Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
            foreach (var gone in installs.Where(i => serverKeys.Contains(i.ServerKey) && !keep.Contains((i.ApplicationId, i.ServerKey))))
            {
                removed++;
                changes.Add(new(names.GetValueOrDefault(gone.ApplicationId) ?? "", gone.Server, "Removed", gone.Version, null));
                if (!apply) continue;
                db.InvHistory.Add(new InvHistory { ApplicationId = gone.ApplicationId, Server = gone.Server, ServerKey = gone.ServerKey, Kind = InvChange.Removed, From = gone.Version, At = now, By = by });
                db.InvInstalls.Remove(gone);
            }
        }
        if (apply) await db.SaveChangesAsync(ct);
        return new SyncResultDto(added, changed, unchanged, removed, newApps, changes);
    }

    /// <summary>
    /// Rows of a CSV file with a header row naming Application, Server and Version (Vendor too, if it is there), in any order.
    /// Quoted fields may hold commas, quotes ("") and line breaks.
    /// </summary>
    public static Outcome<IReadOnlyList<SyncRowDto>> ReadCsv(string text)
    {
        var records = ParseCsv(text ?? "").Where(r => r.Any(f => f.Length > 0)).ToList();
        if (records.Count == 0) return Problem.Invalid("csv", "The file is empty.");
        var head = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => head.IndexOf(name);
        var (app, vendor, server, version) = (Col("application"), Col("vendor"), Col("server"), Col("version"));
        if (app < 0 || server < 0 || version < 0) return Problem.Invalid("csv", "The header needs Application, Server and Version.");
        string Field(List<string> r, int i) => i >= 0 && i < r.Count ? r[i] : "";
        return Outcome.Of<IReadOnlyList<SyncRowDto>>([.. records.Skip(1).Select(r => new SyncRowDto(Field(r, app), vendor < 0 ? null : Field(r, vendor), Field(r, server), Field(r, version)))]);
    }

    private static IEnumerable<List<string>> ParseCsv(string text)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                yield return record;
                record = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); yield return record; }
    }
}
