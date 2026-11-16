using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>The site switches for page comments and ratings (comments.md, Switching comments on and off).</summary>
public sealed record FeedbackSettingsDto(bool CommentsEnabled, bool RatingsEnabled);

/// <summary>One feature's switch at each level, and the result: on only if all three are on (question Q-0025).</summary>
public sealed record FeedbackLevelsDto(bool Site, bool Space, bool Page, bool Effective);

public sealed record PageFeedbackDto(FeedbackLevelsDto Comments, FeedbackLevelsDto Ratings);

[Action("feedback.settings.read", Read = true)]
public sealed record ReadFeedbackSettings : IAction<FeedbackSettingsDto>;

[Action("feedback.settings.save")]
public sealed record SaveFeedbackSettings : IAction<FeedbackSettingsDto>
{
    public required FeedbackSettingsDto Settings { get; init; }
}

[Action("page.feedback.read", Read = true)]
public sealed record ReadPageFeedback : IAction<PageFeedbackDto>
{
    public required Guid PageId { get; init; }
}

/// <summary>Sets the page's switches. Null leaves one as it is.</summary>
[Action("page.feedback.save")]
public sealed record SavePageFeedback : IAction<PageFeedbackDto>
{
    public required Guid PageId { get; init; }
    public bool? CommentsEnabled { get; init; }
    public bool? RatingsEnabled { get; init; }
}

/// <summary>What comments and ratings share: the switches and who moderates.</summary>
internal static class Feedback
{
    public static async Task<FeedbackSettingsDto> SiteAsync(KbDbContext db, CancellationToken ct) =>
        new(await db.GetAsync(RuntimeSettingKeys.FeedbackCommentsEnabled, ct) != "false", await db.GetAsync(RuntimeSettingKeys.FeedbackRatingsEnabled, ct) != "false");

    /// <summary>The switches for a page at each level. Off at a level cannot be overridden below it (question Q-0025).</summary>
    public static async Task<PageFeedbackDto> LevelsAsync(KbDbContext db, Page page, CancellationToken ct)
    {
        var site = await SiteAsync(db, ct);
        var space = await db.Spaces.Where(s => s.Id == page.SpaceId).Select(s => new { s.CommentsEnabled, s.RatingsEnabled }).FirstAsync(ct);
        return new(Levels(site.CommentsEnabled, space.CommentsEnabled, page.CommentsEnabled), Levels(site.RatingsEnabled, space.RatingsEnabled, page.RatingsEnabled));
    }

    private static FeedbackLevelsDto Levels(bool site, bool space, bool page) => new(site, space, page, site && space && page);

    /// <summary>
    /// The caller moderates the space: a signed-in person (no token) who holds the Supervisor or Owner role there. Being an Admin is not
    /// enough on its own (comments.md, Who may do what; question Q-0024).
    /// </summary>
    public static async Task<bool> ModeratesAsync(SpacePermissionService permissions, Actor actor, Guid spaceId, CancellationToken ct) =>
        actor is { Kind: ActorKind.User, UserId: { } user, TokenId: null } && await permissions.GetHeldRoleAsync(user, spaceId, ct) >= SpaceRole.Supervisor;
}

internal sealed class ReadFeedbackSettingsHandler(KbDbContext db, IAccess access) : IActionHandler<ReadFeedbackSettings, FeedbackSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadFeedbackSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<FeedbackSettingsDto>> HandleAsync(ActionContext<ReadFeedbackSettings> ctx) => await Feedback.SiteAsync(db, ctx.Cancel);
}

internal sealed class SaveFeedbackSettingsHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SaveFeedbackSettings, FeedbackSettingsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveFeedbackSettings> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<FeedbackSettingsDto>> HandleAsync(ActionContext<SaveFeedbackSettings> ctx)
    {
        var d = ctx.Action.Settings;
        var now = time.GetUtcNow();
        var values = new Dictionary<string, string?>
        {
            [RuntimeSettingKeys.FeedbackCommentsEnabled] = d.CommentsEnabled ? "true" : "false",
            [RuntimeSettingKeys.FeedbackRatingsEnabled] = d.RatingsEnabled ? "true" : "false",
        };
        foreach (var (key, value) in values) await db.SetAsync(key, value, ctx.Actor.UserId, now, ctx.Cancel);
        ctx.Audit(AuditActions.FeedbackSettingsSave, "setting", "Feedback", data: values.ToDictionary(kv => kv.Key, kv => kv.Value ?? ""));
        return d;
    }
}

internal sealed class ReadPageFeedbackHandler(KbDbContext db, IAccess access) : IActionHandler<ReadPageFeedback, PageFeedbackDto>
{
    private Page? _page;

    // Guests get 401 even where they could read the page (question Q-0027); a page the caller cannot read is 404.
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadPageFeedback> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized
        : (_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is null ? Problem.NotFound : null;

    public async Task<Outcome<PageFeedbackDto>> HandleAsync(ActionContext<ReadPageFeedback> ctx) => await Feedback.LevelsAsync(db, _page!, ctx.Cancel);
}

internal sealed class SavePageFeedbackHandler(KbDbContext db, IAccess access, SpacePermissionService permissions) : IActionHandler<SavePageFeedback, PageFeedbackDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SavePageFeedback> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        return await Feedback.ModeratesAsync(permissions, ctx.Actor, _page.SpaceId, ctx.Cancel) ? null : Problem.Forbidden;
    }

    public async Task<Outcome<PageFeedbackDto>> HandleAsync(ActionContext<SavePageFeedback> ctx)
    {
        var (a, page) = (ctx.Action, _page!);
        if (a.CommentsEnabled is { } c) page.CommentsEnabled = c;
        if (a.RatingsEnabled is { } r) page.RatingsEnabled = r;
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.PageFeedbackSave, "page", page.Id.ToString(), page.SpaceId,
            new Dictionary<string, string> { ["commentsEnabled"] = page.CommentsEnabled.ToString(), ["ratingsEnabled"] = page.RatingsEnabled.ToString() });
        return await Feedback.LevelsAsync(db, page, ctx.Cancel);
    }
}
