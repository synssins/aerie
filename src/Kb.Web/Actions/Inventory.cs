using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Inventory;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// The application inventory (core). Who views and who edits is what an Admin sets (InventoryService); every change is audited.
// Kept by hand, by CSV import (preview, then apply) and by inventory.installs.sync, which plugins and scripts send.

public sealed record InvAppRowDto(Guid Id, string Name, string? Vendor, OwnerRefDto? Owner, int Installs);
public sealed record InvHistoryDto(DateTimeOffset At, string By, string Kind, string? From, string? To);
public sealed record InvInstallDto(Guid Id, string Server, string Version, DateTimeOffset ChangedAt, string ChangedBy, IReadOnlyList<InvHistoryDto> History);
public sealed record InvPageDto(Guid Id, string Title, string SpaceName, IReadOnlyList<string> Servers);
public sealed record InvAppDto(Guid Id, string Name, string? Vendor, OwnerRefDto? Owner, string? Notes, IReadOnlyList<InvInstallDto> Installs, IReadOnlyList<InvPageDto> Pages, bool CanEdit);
public sealed record InvMeDto(bool CanView, bool CanEdit);
/// <summary>Kind: Everyone, Space, Role, User or Group. Id: the space id, role id, user id or group key. Name: shown, filled on read.</summary>
public sealed record InvRuleDto(string Kind, string? Id, string? Name = null);
public sealed record InvAccessDto(IReadOnlyList<InvRuleDto> View, IReadOnlyList<InvRuleDto> Edit);
public sealed record InvAppRequest(string? Name, string? Vendor, string? Notes, string? OwnerType, string? OwnerId);
public sealed record InvInstallRequest(string? Server, string? Version);
public sealed record InvImportRequest(string? Csv, bool RemoveMissing, bool Apply);
public sealed record InvSyncRequest(string? Source, IReadOnlyList<SyncRowDto>? Rows, bool RemoveMissing, bool? Apply);
public sealed record PageDetailsRequest(IReadOnlyList<AppliesToRequest>? AppliesTo, string? OwnerType, string? OwnerId, int? ReviewMonths);
public sealed record AppliesToRequest(Guid ApplicationId, IReadOnlyList<string>? Servers);

[Action("inventory.me", Read = true)]
public sealed record ReadInventoryMe : IAction<InvMeDto>;

[Action("inventory.app.list", Read = true)]
public sealed record ListInvApps : IAction<IReadOnlyList<InvAppRowDto>>
{
    public string? Query { get; init; }
    public string? Server { get; init; }
}

[Action("inventory.server.list", Read = true)]
public sealed record ListInvServers : IAction<IReadOnlyList<string>>;

[Action("inventory.app.read", Read = true)]
public sealed record ReadInvApp : IAction<InvAppDto>
{
    public required Guid ApplicationId { get; init; }
}

[Action("inventory.app.create")]
public sealed record CreateInvApp : IAction<InvAppDto>
{
    public required InvAppRequest App { get; init; }
}

[Action("inventory.app.update")]
public sealed record UpdateInvApp : IAction<InvAppDto>
{
    public required Guid ApplicationId { get; init; }
    public required InvAppRequest App { get; init; }
}

/// <summary>Refused while a page applies to the application (the count is given). Its installs and their history go with it.</summary>
[Action("inventory.app.delete")]
public sealed record DeleteInvApp : IAction<Unit>
{
    public required Guid ApplicationId { get; init; }
}

/// <summary>Adds the application on a server, or changes its version there.</summary>
[Action("inventory.install.set")]
public sealed record SetInvInstall : IAction<InvAppDto>
{
    public required Guid ApplicationId { get; init; }
    public required string Server { get; init; }
    public required string Version { get; init; }
}

[Action("inventory.install.remove")]
public sealed record RemoveInvInstall : IAction<Unit>
{
    public required Guid InstallId { get; init; }
}

/// <summary>A CSV file of installs: Apply false previews. The same update as a sync, named "CSV import" in the history.</summary>
[Action("inventory.import")]
public sealed record ImportInventory : IAction<SyncResultDto>
{
    public required string Csv { get; init; }
    public bool RemoveMissing { get; init; }
    public bool Apply { get; init; }
}

[Action("inventory.access.read", Read = true)]
public sealed record ReadInventoryAccess : IAction<InvAccessDto>;

[Action("inventory.access.save")]
public sealed record SaveInventoryAccess : IAction<InvAccessDto>
{
    public required InvAccessDto Access { get; init; }
}

/// <summary>A page's details: what it applies to, its owner, its review cycle. One who may edit the page sets them.</summary>
[Action("page.details.save")]
public sealed record SavePageDetails : IAction<PageDetailsDto>
{
    public required Guid PageId { get; init; }
    public required PageDetailsRequest Details { get; init; }
}

internal static class InventorySupport
{
    internal static readonly int[] ReviewChoices = [1, 3, 6, 12, 24, 36];

    internal static async Task<string> WhoAsync(KbDbContext db, Actor actor, CancellationToken ct) =>
        actor.UserId is { } id ? await db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "Unknown"
        : actor.PluginId is { } plugin ? plugin : "System";

    /// <summary>The owner's name: a person's display name or a group's name. Null when there is none, or it is gone.</summary>
    internal static async Task<OwnerRefDto?> OwnerAsync(KbDbContext db, PrincipalType? type, string? id, CancellationToken ct)
    {
        if (type is null || string.IsNullOrEmpty(id)) return null;
        string? name = type == PrincipalType.User
            ? Guid.TryParse(id, out var uid) ? await db.Users.Where(u => u.Id == uid && u.DeletedAt == null).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null
            : await db.KbGroups.Where(g => g.Key == id).Select(g => g.Name).FirstOrDefaultAsync(ct);
        return name is null ? null : new OwnerRefDto(type.Value.ToString(), id, name);
    }

    /// <summary>Checks an owner given as type and id: a person who exists or a group that exists, or none at all.</summary>
    internal static async Task<(PrincipalType? Type, string? Id, Problem? Problem)> CheckOwnerAsync(KbDbContext db, string? type, string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(id)) return (null, null, null);
        if (!Enum.TryParse<PrincipalType>(type, out var t) || !Enum.IsDefined(t) || string.IsNullOrWhiteSpace(id)) return (null, null, Problem.Invalid("owner", "A person or a group."));
        return await OwnerAsync(db, t, id, ct) is null ? (null, null, Problem.Invalid("owner", "No such person or group.")) : (t, id, null);
    }

    internal static IReadOnlyList<string> Servers(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static async Task<InvAppDto> ToDtoAsync(KbDbContext db, IAccess access, Actor actor, InvApplication a, bool canEdit, CancellationToken ct)
    {
        var installs = await db.InvInstalls.AsNoTracking().Where(i => i.ApplicationId == a.Id).OrderBy(i => i.ServerKey).ToListAsync(ct);
        var history = (await db.InvHistory.AsNoTracking().Where(h => h.ApplicationId == a.Id).ToListAsync(ct))
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Kind).ToLookup(h => h.ServerKey);
        // Only the pages the caller may read are listed.
        var visible = await access.VisibleSpacesAsync(actor, ct);
        var pages = await (from l in db.PageAppLinks.AsNoTracking()
                           where l.ApplicationId == a.Id
                           join p in db.Pages.AsNoTracking().In(new PageScope(visible)) on l.PageId equals p.Id
                           join s in db.Spaces.AsNoTracking() on p.SpaceId equals s.Id
                           orderby p.Title
                           select new { p.Id, p.Title, Space = s.Name, l.ServersJson }).ToListAsync(ct);
        return new InvAppDto(a.Id, a.Name, a.Vendor, await OwnerAsync(db, a.OwnerType, a.OwnerId, ct), a.Notes,
            [.. installs.Select(i => new InvInstallDto(i.Id, i.Server, i.Version, i.ChangedAt, i.ChangedBy,
                [.. history[i.ServerKey].Select(h => new InvHistoryDto(h.At, h.By, h.Kind.ToString(), h.From, h.To))]))],
            [.. pages.Select(p => new InvPageDto(p.Id, p.Title, p.Space, Servers(p.ServersJson)))], canEdit);
    }

    internal static async Task<PageDetailsDto> PageDetailsAsync(KbDbContext db, Kb.Core.Content.Page page, CancellationToken ct)
    {
        var links = await (from l in db.PageAppLinks.AsNoTracking()
                           where l.PageId == page.Id
                           join a in db.InvApplications.AsNoTracking() on l.ApplicationId equals a.Id
                           orderby a.Name
                           select new { a.Id, a.Name, l.ServersJson }).ToListAsync(ct);
        return new PageDetailsDto([.. links.Select(l => new AppliesToDto(l.Id, l.Name, Servers(l.ServersJson)))], await OwnerAsync(db, page.OwnerType, page.OwnerId, ct), page.ReviewMonths);
    }

    /// <summary>The checks of an application's fields.</summary>
    internal static async Task<(InvAppRequest Clean, PrincipalType? OwnerType, string? OwnerId, Problem? Problem)> CheckAppAsync(KbDbContext db, InvAppRequest req, Guid? self, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length is 0 or > 200) return (req, null, null, Problem.Invalid("name", "1-200 characters."));
        var key = InventoryService.Key(name);
        if (await db.InvApplications.AnyAsync(a => a.NameKey == key && a.Id != self, ct)) return (req, null, null, Problem.Invalid("name", "Name in use."));
        if ((req.Vendor?.Trim().Length ?? 0) > 200) return (req, null, null, Problem.Invalid("vendor", "200 characters at most."));
        if ((req.Notes?.Length ?? 0) > 4000) return (req, null, null, Problem.Invalid("notes", "4000 characters at most."));
        var (type, id, owner) = await CheckOwnerAsync(db, req.OwnerType, req.OwnerId, ct);
        if (owner is not null) return (req, null, null, owner);
        string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return (new InvAppRequest(name, Blank(req.Vendor), Blank(req.Notes), req.OwnerType, req.OwnerId), type, id, null);
    }
}

internal sealed class ReadInventoryMeHandler(InventoryService inventory) : IActionHandler<ReadInventoryMe, InvMeDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadInventoryMe> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<InvMeDto>> HandleAsync(ActionContext<ReadInventoryMe> ctx) =>
        new InvMeDto(await inventory.CanViewAsync(ctx.Actor, ctx.Cancel), await inventory.CanEditAsync(ctx.Actor, ctx.Cancel));
}

/// <summary>Views: 401 for nobody, 403 for one the Admin's rules leave out.</summary>
internal static class InvGate
{
    internal static async Task<Problem?> ViewAsync(InventoryService inventory, Actor actor, CancellationToken ct) =>
        actor.UserId is null && actor.Kind != ActorKind.Plugin ? Problem.Unauthorized : await inventory.CanViewAsync(actor, ct) ? null : Problem.Forbidden;

    internal static async Task<Problem?> EditAsync(InventoryService inventory, Actor actor, CancellationToken ct) =>
        actor.UserId is null && actor.Kind != ActorKind.Plugin ? Problem.Unauthorized : await inventory.CanEditAsync(actor, ct) ? null : Problem.Forbidden;
}

internal sealed class ListInvAppsHandler(KbDbContext db, InventoryService inventory) : IActionHandler<ListInvApps, IReadOnlyList<InvAppRowDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListInvApps> ctx) => InvGate.ViewAsync(inventory, ctx.Actor, ctx.Cancel);

    public async Task<Outcome<IReadOnlyList<InvAppRowDto>>> HandleAsync(ActionContext<ListInvApps> ctx)
    {
        var q = db.InvApplications.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(ctx.Action.Query))
        {
            var text = InventoryService.Key(ctx.Action.Query);
            q = q.Where(a => a.NameKey.Contains(text) || (a.Vendor != null && EF.Functions.Like(a.Vendor, "%" + text + "%")));
        }
        if (!string.IsNullOrWhiteSpace(ctx.Action.Server))
        {
            var server = InventoryService.Key(ctx.Action.Server);
            q = q.Where(a => db.InvInstalls.Any(i => i.ApplicationId == a.Id && i.ServerKey == server));
        }
        var apps = await q.OrderBy(a => a.NameKey).Select(a => new { a, Installs = db.InvInstalls.Count(i => i.ApplicationId == a.Id) }).Take(1000).ToListAsync(ctx.Cancel);
        var list = new List<InvAppRowDto>();
        foreach (var x in apps) list.Add(new InvAppRowDto(x.a.Id, x.a.Name, x.a.Vendor, await InventorySupport.OwnerAsync(db, x.a.OwnerType, x.a.OwnerId, ctx.Cancel), x.Installs));
        return Outcome.List<InvAppRowDto>(list);
    }
}

internal sealed class ListInvServersHandler(KbDbContext db, InventoryService inventory) : IActionHandler<ListInvServers, IReadOnlyList<string>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListInvServers> ctx) => InvGate.ViewAsync(inventory, ctx.Actor, ctx.Cancel);

    public async Task<Outcome<IReadOnlyList<string>>> HandleAsync(ActionContext<ListInvServers> ctx)
    {
        var all = await db.InvInstalls.AsNoTracking().Select(i => new { i.ServerKey, i.Server }).ToListAsync(ctx.Cancel);
        return Outcome.List<string>([.. all.GroupBy(i => i.ServerKey).OrderBy(g => g.Key).Select(g => g.First().Server)]);
    }
}

internal sealed class ReadInvAppHandler(KbDbContext db, IAccess access, InventoryService inventory) : IActionHandler<ReadInvApp, InvAppDto>
{
    private InvApplication? _app;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadInvApp> ctx)
    {
        if (await InvGate.ViewAsync(inventory, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        return (_app = await db.InvApplications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == ctx.Action.ApplicationId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<InvAppDto>> HandleAsync(ActionContext<ReadInvApp> ctx) =>
        await InventorySupport.ToDtoAsync(db, access, ctx.Actor, _app!, await inventory.CanEditAsync(ctx.Actor, ctx.Cancel), ctx.Cancel);
}

internal sealed class CreateInvAppHandler(KbDbContext db, IAccess access, InventoryService inventory, TimeProvider time) : IActionHandler<CreateInvApp, InvAppDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<CreateInvApp> ctx) => InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel);

    public async Task<Outcome<InvAppDto>> HandleAsync(ActionContext<CreateInvApp> ctx)
    {
        var (clean, type, id, problem) = await InventorySupport.CheckAppAsync(db, ctx.Action.App, null, ctx.Cancel);
        if (problem is not null) return problem;
        var now = time.GetUtcNow();
        var app = new InvApplication { Name = clean.Name!, NameKey = InventoryService.Key(clean.Name!), Vendor = clean.Vendor, Notes = clean.Notes, OwnerType = type, OwnerId = id, CreatedAt = now, UpdatedAt = now };
        db.InvApplications.Add(app);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.InventoryAppCreate, "application", app.Id.ToString(), data: new Dictionary<string, string> { ["name"] = app.Name });
        return await InventorySupport.ToDtoAsync(db, access, ctx.Actor, app, true, ctx.Cancel);
    }
}

internal sealed class UpdateInvAppHandler(KbDbContext db, IAccess access, InventoryService inventory, TimeProvider time) : IActionHandler<UpdateInvApp, InvAppDto>
{
    private InvApplication? _app;

    public async Task<Problem?> AuthorizeAsync(ActionContext<UpdateInvApp> ctx)
    {
        if (await InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        return (_app = await db.InvApplications.FirstOrDefaultAsync(a => a.Id == ctx.Action.ApplicationId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<InvAppDto>> HandleAsync(ActionContext<UpdateInvApp> ctx)
    {
        var app = _app!;
        var (clean, type, id, problem) = await InventorySupport.CheckAppAsync(db, ctx.Action.App, app.Id, ctx.Cancel);
        if (problem is not null) return problem;
        (app.Name, app.NameKey, app.Vendor, app.Notes, app.OwnerType, app.OwnerId, app.UpdatedAt) =
            (clean.Name!, InventoryService.Key(clean.Name!), clean.Vendor, clean.Notes, type, id, time.GetUtcNow());
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.InventoryAppUpdate, "application", app.Id.ToString(), data: new Dictionary<string, string> { ["name"] = app.Name });
        return await InventorySupport.ToDtoAsync(db, access, ctx.Actor, app, true, ctx.Cancel);
    }
}

internal sealed class DeleteInvAppHandler(KbDbContext db, InventoryService inventory) : IActionHandler<DeleteInvApp, Unit>
{
    private InvApplication? _app;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DeleteInvApp> ctx)
    {
        if (await InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        return (_app = await db.InvApplications.FirstOrDefaultAsync(a => a.Id == ctx.Action.ApplicationId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteInvApp> ctx)
    {
        var app = _app!;
        // Pages in the recycle bin do not count (the bin hides them from every query); their links go with the application.
        var used = await db.PageAppLinks.Where(l => l.ApplicationId == app.Id).Join(db.Pages, l => l.PageId, p => p.Id, (l, p) => p.Id).CountAsync(ctx.Cancel); // system: every page, whoever asks
        if (used > 0) return Problem.Conflict(used == 1 ? "Used on 1 page." : $"Used on {used} pages.");
        await db.PageAppLinks.Where(l => l.ApplicationId == app.Id).ExecuteDeleteAsync(ctx.Cancel);
        await db.InvInstalls.Where(i => i.ApplicationId == app.Id).ExecuteDeleteAsync(ctx.Cancel);
        await db.InvHistory.Where(h => h.ApplicationId == app.Id).ExecuteDeleteAsync(ctx.Cancel);
        db.InvApplications.Remove(app);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.InventoryAppDelete, "application", app.Id.ToString(), data: new Dictionary<string, string> { ["name"] = app.Name });
        return new Unit();
    }
}

internal sealed class SetInvInstallHandler(KbDbContext db, IAccess access, InventoryService inventory) : IActionHandler<SetInvInstall, InvAppDto>
{
    private InvApplication? _app;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SetInvInstall> ctx)
    {
        if (await InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        return (_app = await db.InvApplications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == ctx.Action.ApplicationId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<InvAppDto>> HandleAsync(ActionContext<SetInvInstall> ctx)
    {
        var app = _app!;
        var by = await InventorySupport.WhoAsync(db, ctx.Actor, ctx.Cancel);
        // By hand is the same update as a sync of one row.
        var result = await inventory.SyncAsync([new SyncRowDto(app.Name, null, ctx.Action.Server ?? "", ctx.Action.Version ?? "")], false, true, by, ctx.Cancel);
        if (result.Problem is { } problem) return problem;
        var change = result.Value!.Changes.Count > 0 ? result.Value.Changes[0] : null;
        if (change is not null)
            ctx.Audit(AuditActions.InventoryInstallSet, "application", app.Id.ToString(), data: new Dictionary<string, string>
                { ["server"] = change.Server, ["from"] = change.From ?? "", ["to"] = change.To ?? "" });
        return await InventorySupport.ToDtoAsync(db, access, ctx.Actor, app, true, ctx.Cancel);
    }
}

internal sealed class RemoveInvInstallHandler(KbDbContext db, InventoryService inventory, TimeProvider time) : IActionHandler<RemoveInvInstall, Unit>
{
    private InvInstall? _install;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveInvInstall> ctx)
    {
        if (await InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel) is { } refused) return refused;
        return (_install = await db.InvInstalls.FirstOrDefaultAsync(i => i.Id == ctx.Action.InstallId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveInvInstall> ctx)
    {
        var i = _install!;
        db.InvHistory.Add(new InvHistory { ApplicationId = i.ApplicationId, Server = i.Server, ServerKey = i.ServerKey, Kind = InvChange.Removed, From = i.Version, At = time.GetUtcNow(),
            By = await InventorySupport.WhoAsync(db, ctx.Actor, ctx.Cancel) });
        db.InvInstalls.Remove(i);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.InventoryInstallRemove, "application", i.ApplicationId.ToString(), data: new Dictionary<string, string> { ["server"] = i.Server, ["version"] = i.Version });
        return new Unit();
    }
}

internal sealed class ImportInventoryHandler(KbDbContext db, InventoryService inventory) : IActionHandler<ImportInventory, SyncResultDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ImportInventory> ctx) => InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel);

    public async Task<Outcome<SyncResultDto>> HandleAsync(ActionContext<ImportInventory> ctx)
    {
        var req = ctx.Action;
        if (req.Csv.Length > 20_000_000) return Problem.Invalid("csv", "20 MB at most.");
        var rows = InventoryService.ReadCsv(req.Csv);
        if (rows.Problem is { } bad) return bad;
        var by = $"CSV import ({await InventorySupport.WhoAsync(db, ctx.Actor, ctx.Cancel)})";
        var result = await inventory.SyncAsync(rows.Value!, req.RemoveMissing, req.Apply, by, ctx.Cancel);
        if (result.Ok && req.Apply) ctx.Audit(AuditActions.InventoryImport, "inventory", null, data: Counts(result.Value!, req.RemoveMissing));
        return result;
    }

    internal static Dictionary<string, string> Counts(SyncResultDto r, bool removeMissing) => new()
    {
        ["added"] = r.Added.ToString(System.Globalization.CultureInfo.InvariantCulture), ["changed"] = r.Changed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["removed"] = r.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture), ["newApplications"] = r.NewApplications.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["removeMissing"] = removeMissing.ToString(),
    };
}

internal sealed class SyncInstallsHandler(KbDbContext db, InventoryService inventory) : IActionHandler<SyncInstalls, SyncResultDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<SyncInstalls> ctx) => InvGate.EditAsync(inventory, ctx.Actor, ctx.Cancel);

    public async Task<Outcome<SyncResultDto>> HandleAsync(ActionContext<SyncInstalls> ctx)
    {
        var req = ctx.Action;
        var source = (req.Source ?? "").Trim();
        if (source.Length is 0 or > 100) return Problem.Invalid("source", "1-100 characters.");
        var by = $"Sync: {source} ({await InventorySupport.WhoAsync(db, ctx.Actor, ctx.Cancel)})";
        var result = await inventory.SyncAsync(req.Rows ?? [], req.RemoveMissing, req.Apply, by, ctx.Cancel);
        if (result.Ok && req.Apply)
        {
            var data = ImportInventoryHandler.Counts(result.Value!, req.RemoveMissing);
            data["source"] = source;
            ctx.Audit(AuditActions.InventorySync, "inventory", null, data: data);
        }
        return result;
    }
}

internal sealed class ReadInventoryAccessHandler(KbDbContext db, IAccess access) : IActionHandler<ReadInventoryAccess, InvAccessDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadInventoryAccess> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<InvAccessDto>> HandleAsync(ActionContext<ReadInventoryAccess> ctx) => await InventoryAccess.ReadAsync(db, ctx.Cancel);
}

internal static class InventoryAccess
{
    internal static async Task<InvAccessDto> ReadAsync(KbDbContext db, CancellationToken ct)
    {
        var rules = await db.InvAccessRules.AsNoTracking().ToListAsync(ct);
        var spaces = await db.Spaces.AsNoTracking().ToDictionaryAsync(s => s.Id.ToString("D"), s => s.Name, ct);
        var roles = await db.SpaceRoleDefs.AsNoTracking().ToDictionaryAsync(r => r.Id.ToString("D"), r => r.Name, ct);
        var groups = await db.KbGroups.AsNoTracking().ToDictionaryAsync(g => g.Key, g => g.Name, ct);
        var userIds = rules.Where(r => r.Kind == InvRuleKind.User && Guid.TryParse(r.TargetId, out _)).Select(r => Guid.Parse(r.TargetId!)).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id.ToString("D"), u => u.DisplayName, ct);
        string? Name(InvAccessRule r) => r.Kind switch
        {
            InvRuleKind.Space => spaces.GetValueOrDefault(r.TargetId ?? ""), InvRuleKind.Role => roles.GetValueOrDefault(r.TargetId ?? ""),
            InvRuleKind.Group => groups.GetValueOrDefault(r.TargetId ?? ""), InvRuleKind.User => users.GetValueOrDefault(r.TargetId ?? ""), _ => null,
        };
        InvRuleDto Dto(InvAccessRule r) => new(r.Kind.ToString(), r.TargetId, Name(r));
        return new InvAccessDto([.. rules.Where(r => !r.Edit).Select(Dto)], [.. rules.Where(r => r.Edit).Select(Dto)]);
    }
}

internal sealed class SaveInventoryAccessHandler(KbDbContext db, IAccess access) : IActionHandler<SaveInventoryAccess, InvAccessDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveInventoryAccess> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<InvAccessDto>> HandleAsync(ActionContext<SaveInventoryAccess> ctx)
    {
        var req = ctx.Action.Access;
        var rules = new List<InvAccessRule>();
        foreach (var (list, edit) in new[] { (req.View ?? [], false), (req.Edit ?? [], true) })
            foreach (var r in list)
            {
                if (!Enum.TryParse<InvRuleKind>(r.Kind, out var kind) || !Enum.IsDefined(kind)) return Problem.Invalid("kind", "Everyone, Space, Role, User or Group.");
                var id = kind == InvRuleKind.Everyone ? null : r.Id?.Trim();
                var exists = kind switch
                {
                    InvRuleKind.Everyone => true,
                    InvRuleKind.Space => Guid.TryParse(id, out var s) && await db.Spaces.AnyAsync(x => x.Id == s, ctx.Cancel),
                    InvRuleKind.Role => Guid.TryParse(id, out var ro) && await db.SpaceRoleDefs.AnyAsync(x => x.Id == ro, ctx.Cancel),
                    InvRuleKind.User => Guid.TryParse(id, out var u) && await db.Users.AnyAsync(x => x.Id == u && x.DeletedAt == null, ctx.Cancel),
                    _ => id is not null && await db.KbGroups.AnyAsync(x => x.Key == id, ctx.Cancel),
                };
                if (!exists) return Problem.Invalid("id", $"No such {kind.ToString().ToLowerInvariant()}.");
                if (!rules.Any(x => x.Edit == edit && x.Kind == kind && x.TargetId == id)) rules.Add(new InvAccessRule { Edit = edit, Kind = kind, TargetId = id });
            }
        db.InvAccessRules.RemoveRange(await db.InvAccessRules.ToListAsync(ctx.Cancel));
        db.InvAccessRules.AddRange(rules);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.InventoryAccessSet, "inventory", null, data: new Dictionary<string, string>
        {
            ["view"] = string.Join(",", rules.Where(r => !r.Edit).Select(r => $"{r.Kind}:{r.TargetId}")),
            ["edit"] = string.Join(",", rules.Where(r => r.Edit).Select(r => $"{r.Kind}:{r.TargetId}")),
        });
        return await InventoryAccess.ReadAsync(db, ctx.Cancel);
    }
}

internal sealed class SavePageDetailsHandler(KbDbContext db, IAccess access) : IActionHandler<SavePageDetails, PageDetailsDto>
{
    private Kb.Core.Content.Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SavePageDetails> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<PageDetailsDto>> HandleAsync(ActionContext<SavePageDetails> ctx)
    {
        var (page, req) = (_page!, ctx.Action.Details);
        if (req.ReviewMonths is { } months && !InventorySupport.ReviewChoices.Contains(months)) return Problem.Invalid("reviewMonths", "1, 3, 6, 12, 24 or 36.");
        var (type, id, owner) = await InventorySupport.CheckOwnerAsync(db, req.OwnerType, req.OwnerId, ctx.Cancel);
        if (owner is not null) return owner;
        var wanted = (req.AppliesTo ?? []).GroupBy(a => a.ApplicationId).Select(g => g.Last()).ToList();
        if (wanted.Count > 100) return Problem.Invalid("appliesTo", "100 at most.");
        var links = new List<Kb.Core.Inventory.PageAppLink>();
        foreach (var a in wanted)
        {
            if (!await db.InvApplications.AnyAsync(x => x.Id == a.ApplicationId, ctx.Cancel)) return Problem.Invalid("appliesTo", "No such application.");
            // A server is one the application is on, named as the inventory names it.
            var installed = await db.InvInstalls.Where(i => i.ApplicationId == a.ApplicationId).ToDictionaryAsync(i => i.ServerKey, i => i.Server, ctx.Cancel);
            var servers = new List<string>();
            foreach (var s in a.Servers ?? [])
            {
                if (!installed.TryGetValue(InventoryService.Key(s), out var name)) return Problem.Invalid("appliesTo", $"Not on server {s}.");
                if (!servers.Contains(name)) servers.Add(name);
            }
            links.Add(new Kb.Core.Inventory.PageAppLink { PageId = page.Id, ApplicationId = a.ApplicationId, ServersJson = JsonSerializer.Serialize(servers) });
        }
        db.PageAppLinks.RemoveRange(await db.PageAppLinks.Where(l => l.PageId == page.Id).ToListAsync(ctx.Cancel));
        db.PageAppLinks.AddRange(links);
        (page.OwnerType, page.OwnerId, page.ReviewMonths) = (type, id, req.ReviewMonths);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.PageDetailsSet, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string>
        {
            ["appliesTo"] = string.Join(",", links.Select(l => l.ApplicationId)), ["owner"] = id is null ? "" : $"{type}:{id}", ["reviewMonths"] = req.ReviewMonths?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        });
        return await InventorySupport.PageDetailsAsync(db, page, ctx.Cancel);
    }
}
