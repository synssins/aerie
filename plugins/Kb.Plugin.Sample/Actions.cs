using Kb.Plugins;
using Kb.Plugins.Actions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kb.Plugin.Sample;

// #region guide:actions
/// <summary>The caller has read this page. Counted once per person.</summary>
[Action("sample.ack")]
public sealed record Acknowledge : IAction<AckCount>
{
    public required Guid PageId { get; init; }
}

[Action("sample.ack.count", Read = true)]
public sealed record CountAcks : IAction<AckCount>
{
    public required Guid PageId { get; init; }
}

/// <summary>Who acknowledged the page. For people who hold the plugin's permission "see-acknowledgements" in the page's space.</summary>
[Action("sample.acks.read", Read = true)]
public sealed record ReadAcks : IAction<AckList>
{
    public required Guid PageId { get; init; }
}

/// <summary>How many acknowledgements are kept, on how many pages. For Admins: the plugin's panel on its page in Administration > Plugins.</summary>
[Action("sample.acks.totals", Read = true)]
public sealed record ReadAckTotals : IAction<AckTotals>;

public sealed record AckCount(int Count, bool Mine);
public sealed record AckTotals(int Acknowledgements, int Pages);
public sealed record AckEntry(string Name, string Version, DateTime AtUtc);
public sealed record AckList(IReadOnlyList<AckEntry> People);
// #endregion

// #region guide:handler
/// <summary>
/// A plugin reaches core data through the bus, as its caller. Sending page.read answers 404 for a page the caller may not see,
/// so this handler needs no permission logic of its own to stay inside what the caller is allowed.
/// </summary>
public sealed class AcknowledgeHandler(SampleData db, IActionBus bus) : IActionHandler<Acknowledge, AckCount>
{
    private PageDto? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<Acknowledge> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        var page = await bus.SendAsync(new ReadPage { PageId = ctx.Action.PageId }, ctx.Cancel);
        _page = page.Value;
        return page.Problem;
    }

    public async Task<Outcome<AckCount>> HandleAsync(ActionContext<Acknowledge> ctx)
    {
        var (page, me) = (_page!, ctx.Actor.UserId!.Value);
        if (!await db.Acks.AnyAsync(a => a.PageId == page.Id && a.UserId == me, ctx.Cancel))
        {
            var who = await bus.SendAsync(new ReadMe(), ctx.Cancel);
            db.Acks.Add(new Ack { PageId = page.Id, UserId = me, Name = who.Value?.DisplayName ?? "Unknown", Version = page.Version, AtUtc = DateTime.UtcNow });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { } // the same person acknowledged twice at once; the unique index kept one
        }
        return new AckCount(await db.Acks.CountAsync(a => a.PageId == page.Id), Mine: true);
    }
}
// #endregion

public sealed class CountAcksHandler(SampleData db, IActionBus bus) : IActionHandler<CountAcks, AckCount>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CountAcks> ctx) =>
        (await bus.SendAsync(new ReadPage { PageId = ctx.Action.PageId }, ctx.Cancel)).Problem;

    public async Task<Outcome<AckCount>> HandleAsync(ActionContext<CountAcks> ctx)
    {
        var (page, me) = (ctx.Action.PageId, ctx.Actor.UserId);
        return new AckCount(await db.Acks.CountAsync(a => a.PageId == page, ctx.Cancel), await db.Acks.AnyAsync(a => a.PageId == page && a.UserId == me, ctx.Cancel));
    }
}

/// <summary>The plugin's page is shown to Admins only, but its route is open to any caller: the handler checks for itself.</summary>
public sealed class ReadAckTotalsHandler(SampleData db, IActionBus bus) : IActionHandler<ReadAckTotals, AckTotals>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadAckTotals> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized : (await bus.SendAsync(new ReadMe(), ctx.Cancel)).Value?.IsAdmin == true ? null : Problem.Forbidden;

    public async Task<Outcome<AckTotals>> HandleAsync(ActionContext<ReadAckTotals> ctx) =>
        new AckTotals(await db.Acks.CountAsync(ctx.Cancel), await db.Acks.Select(a => a.PageId).Distinct().CountAsync(ctx.Cancel));
}

// #region guide:permission
public sealed class ReadAcksHandler(SampleData db, IActionBus bus, IAccess access) : IActionHandler<ReadAcks, AckList>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadAcks> ctx)
    {
        var page = await bus.SendAsync(new ReadPage { PageId = ctx.Action.PageId }, ctx.Cancel);
        if (!page.Ok) return page.Problem; // 404: the caller may not even see the page
        // The manifest declares the permission with a default role (Supervisor). An Admin can change that role on the Plugins screen.
        // IAccess answers for the caller: a person, narrowed by an API token when the request came with one.
        return await access.CanAsync(ctx.Actor, "kb.sample:see-acknowledgements", page.Value!.SpaceId, ctx.Cancel) ? null : Problem.Forbidden;
    }

    public async Task<Outcome<AckList>> HandleAsync(ActionContext<ReadAcks> ctx)
    {
        var page = ctx.Action.PageId;
        return new AckList(await db.Acks.Where(a => a.PageId == page).OrderBy(a => a.Name).Select(a => new AckEntry(a.Name, a.Version, a.AtUtc)).ToListAsync(ctx.Cancel));
    }
}
// #endregion

// #region guide:routes
public static class SampleRoutes
{
    /// <summary>Mapped under /api/v1/plugins/kb.sample/. A route binds the request, sends one action and answers.</summary>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/pages/{id:guid}/ack", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new Acknowledge { PageId = id }, ct).ToHttp()).WithAction<Acknowledge>();
        group.MapGet("/pages/{id:guid}/ack", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CountAcks { PageId = id }, ct).ToHttp()).WithAction<CountAcks>();
        group.MapGet("/pages/{id:guid}/acks", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadAcks { PageId = id }, ct).ToHttp()).WithAction<ReadAcks>();
        group.MapGet("/totals", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadAckTotals(), ct).ToHttp()).WithAction<ReadAckTotals>();
    }
}
// #endregion
