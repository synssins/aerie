using System.Text.Json.Serialization;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>The caller's own rating (contracts/behaviour/ratings.md, A rating).</summary>
public sealed record RatingDto(string Value, bool Solved, string? Reason, string Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// What the caller sees of a page's ratings. Readers get the positive and solved counts only, never a total or a score that would reveal negatives;
/// <see cref="Negative"/> is left out of the answer unless the caller sees all (ratings.md, Who sees what; question Q-0029).
/// </summary>
public sealed record RatingSummaryDto(bool Enabled, bool CanRate, bool CanSeeAll, int Positive, int Solved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Negative, RatingDto? Mine);

[Action("rating.summary", Read = true)]
public sealed record ReadRatingSummary : IAction<RatingSummaryDto>
{
    public required Guid PageId { get; init; }
}

/// <summary>Creates or replaces the caller's rating.</summary>
[Action("rating.save")]
public sealed record SaveRating : IAction<RatingSummaryDto>
{
    public required Guid PageId { get; init; }
    public string? Value { get; init; }
    public bool? Solved { get; init; }
    public string? Reason { get; init; }
}

[Action("rating.remove")]
public sealed record RemoveRating : IAction<Unit>
{
    public required Guid PageId { get; init; }
}

/// <summary>At most 30 rating changes per person per minute across the site (ratings.md, Calls).</summary>
public sealed class RatingThrottle(TimeProvider time) : PersonThrottle(time, 30);

internal static class RatingRules
{
    public const int MaxReason = 280;

    public static string ValueName(bool positive) => positive ? "positive" : "negative";

    /// <summary>A guest gets 401, someone who cannot read the page 404 (ratings.md, Who sees what; question Q-0027).</summary>
    public static async Task<(Problem? Problem, Page? Page)> FindAsync(KbDbContext db, IAccess access, Actor actor, Guid pageId, bool track, CancellationToken ct) =>
        actor.UserId is null ? (Problem.Unauthorized, null)
        : await db.FindVisibleAsync(access, actor, pageId, track, ct) is { } page ? (null, page) : (Problem.NotFound, null);

    /// <summary>
    /// The summary for the caller. With ratings off, readers see no counts; Supervisors and Owners still see everything. The caller's own rating
    /// is always theirs to see, so they can remove it.
    /// </summary>
    public static async Task<RatingSummaryDto> SummaryAsync(KbDbContext db, SpacePermissionService permissions, Actor actor, Page page, CancellationToken ct)
    {
        var enabled = (await Feedback.LevelsAsync(db, page, ct)).Ratings.Effective;
        var seesAll = await Feedback.ModeratesAsync(permissions, actor, page.SpaceId, ct);
        var counts = enabled || seesAll
            ? await db.Ratings.Where(r => r.PageId == page.Id && r.Positive).GroupBy(_ => 1).Select(g => new { Positive = g.Count(), Solved = g.Count(r => r.Solved) }).FirstOrDefaultAsync(ct)
            : null;
        var mine = await db.Ratings.AsNoTracking().FirstOrDefaultAsync(r => r.PageId == page.Id && r.UserId == actor.UserId, ct);
        return new RatingSummaryDto(enabled, enabled && actor.TokenId is null && page.Kind == PageKind.Page, seesAll, counts?.Positive ?? 0, counts?.Solved ?? 0, null,
            mine is null ? null : new RatingDto(ValueName(mine.Positive), mine.Solved, mine.Reason, mine.Version, mine.CreatedAt, mine.UpdatedAt));
    }
}

internal sealed class ReadRatingSummaryHandler(KbDbContext db, IAccess access, SpacePermissionService permissions) : IActionHandler<ReadRatingSummary, RatingSummaryDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadRatingSummary> ctx)
    {
        (var problem, _page) = await RatingRules.FindAsync(db, access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<RatingSummaryDto>> HandleAsync(ActionContext<ReadRatingSummary> ctx) => await RatingRules.SummaryAsync(db, permissions, ctx.Actor, _page!, ctx.Cancel);
}

internal sealed class SaveRatingHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time, RatingThrottle throttle)
    : IActionHandler<SaveRating, RatingSummaryDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveRating> ctx)
    {
        (var problem, _page) = await RatingRules.FindAsync(db, access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<RatingSummaryDto>> HandleAsync(ActionContext<SaveRating> ctx)
    {
        var (a, page, user) = (ctx.Action, _page!, ctx.Actor.UserId!.Value);
        if (page.Kind != PageKind.Page) return Problem.Refused("Ratings are on pages, not folders.");
        if (!(await Feedback.LevelsAsync(db, page, ctx.Cancel)).Ratings.Effective) return Problem.Refused("Ratings are off for this page.");
        bool positive;
        if (a.Value == "positive") positive = true;
        else if (a.Value == "negative") positive = false;
        else return Problem.Invalid("value", "positive or negative.");
        // "This solved my problem" goes only with positive; a reason only with negative, 280 characters at most, and empty is none (ratings.md, A rating).
        var solved = a.Solved ?? false;
        if (solved && !positive) return Problem.Invalid("solved", "Only with a positive rating.");
        var reason = string.IsNullOrWhiteSpace(a.Reason) ? null : a.Reason.Trim();
        if (reason is not null && positive) return Problem.Invalid("reason", "Only with a negative rating.");
        if (reason?.Length > RatingRules.MaxReason) return Problem.Invalid("reason", $"{RatingRules.MaxReason} characters at most.");
        if (throttle.Take(user) is { } wait) return Problem.TooMany("Too many rating changes. Wait a minute.", wait);

        var now = time.GetUtcNow();
        var r = await db.Ratings.FirstOrDefaultAsync(x => x.PageId == page.Id && x.UserId == user, ctx.Cancel);
        if (r is null) db.Ratings.Add(r = new Rating { PageId = page.Id, UserId = user, CreatedAt = now });
        (r.Positive, r.Solved, r.Reason, r.Version, r.UpdatedAt) = (positive, solved, reason, page.Version, now);
        await db.SaveChangesAsync(ctx.Cancel);
        // Neither the value nor the reason: the audit log must not show negatives to anyone who could not see them on the page.
        ctx.Audit(AuditActions.RatingSave, "page", page.Id.ToString(), page.SpaceId);
        return await RatingRules.SummaryAsync(db, permissions, ctx.Actor, page, ctx.Cancel);
    }
}

internal sealed class RemoveRatingHandler(KbDbContext db, IAccess access) : IActionHandler<RemoveRating, Unit>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveRating> ctx)
    {
        (var problem, _page) = await RatingRules.FindAsync(db, access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel);
        return problem;
    }

    /// <summary>Deletes the caller's rating for good, also while ratings are off; 204 also when there was none.</summary>
    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveRating> ctx)
    {
        var page = _page!;
        var user = ctx.Actor.UserId!.Value;
        if (await db.Ratings.Where(r => r.PageId == page.Id && r.UserId == user).ExecuteDeleteAsync(ctx.Cancel) > 0)
            ctx.Audit(AuditActions.RatingRemove, "page", page.Id.ToString(), page.SpaceId);
        return new Unit();
    }
}
