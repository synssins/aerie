using System.Collections.Concurrent;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

/// <summary>A comment as the caller sees it (contracts/behaviour/comments.md, Comments). Author and text are null in a placeholder.</summary>
public sealed record CommentDto(Guid Id, Guid PageId, Guid? ParentId, OwnerRefDto? Author, string? Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt,
    string State, bool CanEdit, bool CanDelete, bool CanModerate, IReadOnlyList<CommentDto> Replies);

public sealed record CommentListDto(bool Enabled, bool CanComment, bool CanModerate, int Total, int Limit, int Offset, IReadOnlyList<CommentDto> Comments);

[Action("comment.list", Read = true)]
public sealed record ListComments : IAction<CommentListDto>
{
    public required Guid PageId { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
}

[Action("comment.create")]
public sealed record CreateComment : IAction<CommentDto>
{
    public required Guid PageId { get; init; }
    public string? Text { get; init; }
    public Guid? ParentId { get; init; }
}

[Action("comment.edit")]
public sealed record EditComment : IAction<CommentDto>
{
    public required Guid CommentId { get; init; }
    public string? Text { get; init; }
}

[Action("comment.delete")]
public sealed record DeleteComment : IAction<Unit>
{
    public required Guid CommentId { get; init; }
}

[Action("comment.hide")]
public sealed record HideComment : IAction<Unit>
{
    public required Guid CommentId { get; init; }
}

[Action("comment.restore")]
public sealed record RestoreComment : IAction<Unit>
{
    public required Guid CommentId { get; init; }
}

/// <summary>
/// At most 10 comments per person per minute across the site (comments.md, Posting). The window starts at the person's first comment.
/// shortcut: counted per server, so N servers allow N times as much; move it to the database if abuse appears.
/// </summary>
public sealed class CommentThrottle(TimeProvider time)
{
    public const int PerMinute = 10;
    private readonly ConcurrentDictionary<Guid, (long Start, int Count)> _windows = new();

    /// <summary>Counts a comment; null when it may go ahead, otherwise the seconds until the window ends.</summary>
    public int? Take(Guid user)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        int? wait = null;
        _windows.AddOrUpdate(user, _ => (now, 1), (_, w) =>
        {
            if (now - w.Start >= 60) return (now, 1);
            if (w.Count >= PerMinute) { wait = (int)(60 - (now - w.Start)); return w; }
            return (w.Start, w.Count + 1);
        });
        return wait;
    }
}

/// <summary>What the comment handlers share: the text rules, who sees what, and the DTO.</summary>
internal static class CommentRules
{
    public const int MaxText = 4000;
    public const int MaxReplies = 100;
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(15);
    /// <summary>A moderator's delete is purged this long after it (comments.md, Moderation).</summary>
    public static readonly TimeSpan PurgeAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// The comment is gone: its author deleted it, or a moderator deleted it 30 days ago or more. Nothing restores it, and only a placeholder
    /// stays while it has replies. The purge job removes what is left; until it runs, this answers the same.
    /// </summary>
    public static bool Gone(Comment c, DateTimeOffset now) => c.DeletedByAuthor || c.State == CommentState.Deleted && c.ModeratedAt + PurgeAfter <= now;

    /// <summary>The text trimmed, or null when it is empty after trimming or longer than 4000 characters (question Q-0023).</summary>
    public static string? Clean(string? text) => text?.Trim() is { Length: > 0 and <= MaxText } t ? t : null;

    public static Problem BadText => Problem.Invalid("text", $"1 to {MaxText} characters.");

    public static string StateName(CommentState s) => s switch { CommentState.Hidden => "hidden", CommentState.Deleted => "deleted", _ => "visible" };

    /// <summary>The comment and its page, when the caller can read the page; otherwise null (404).</summary>
    public static async Task<(Comment Comment, Page Page)?> FindAsync(KbDbContext db, IAccess access, Actor actor, Guid id, bool track, CancellationToken ct)
    {
        var c = track ? await db.Comments.FirstOrDefaultAsync(x => x.Id == id, ct) : await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        return await db.FindVisibleAsync(access, actor, c.PageId, track: false, ct) is { } page ? (c, page) : null;
    }

    /// <summary>The caller and what they may do on this page's comments.</summary>
    public sealed record Viewer(Guid? User, bool Session, bool Enabled, bool Moderates, DateTimeOffset Now);

    public static async Task<Viewer> ViewerAsync(KbDbContext db, SpacePermissionService permissions, Actor actor, Page page, TimeProvider time, CancellationToken ct)
    {
        var enabled = (await Feedback.LevelsAsync(db, page, ct)).Comments.Effective;
        return new(actor.UserId, actor.TokenId is null, enabled, await Feedback.ModeratesAsync(permissions, actor, page.SpaceId, ct), time.GetUtcNow());
    }

    public static bool CanEdit(Viewer v, Comment c) =>
        v.Session && v.Enabled && c.AuthorId == v.User && c.State == CommentState.Visible && !Gone(c, v.Now) && v.Now - c.CreatedAt <= EditWindow;

    /// <summary>The author deletes their own comment; a moderator deletes anyone's.</summary>
    public static bool CanDelete(Viewer v, Comment c) => v.Session && !Gone(c, v.Now) && (c.AuthorId == v.User || v.Moderates);

    /// <summary>
    /// The page's comments as this caller sees them (comments.md, Listing). Moderators see every comment with its state; readers see visible ones
    /// and their own hidden ones. A hidden or deleted top-level comment that still has replies the caller sees stays as a placeholder.
    /// A comment its author deleted is a placeholder for everyone, moderators too.
    /// </summary>
    public static async Task<IReadOnlyList<CommentDto>> TopLevelAsync(KbDbContext db, Viewer v, Guid pageId, CancellationToken ct)
    {
        // shortcut: loads the page's comments at once (replies are capped at 100 a comment); page in the database if pages grow thousands of comments.
        var all = await db.Comments.AsNoTracking().Where(c => c.PageId == pageId).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var ids = all.Select(c => c.AuthorId).Distinct().ToList();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        bool Full(Comment c) => !Gone(c, v.Now) && (v.Moderates || c.State == CommentState.Visible || c.State == CommentState.Hidden && c.AuthorId == v.User);
        CommentDto Dto(Comment c, bool full, IReadOnlyList<CommentDto> replies) => new(c.Id, c.PageId, c.ParentId,
            full ? new OwnerRefDto("user", c.AuthorId.ToString(), names.GetValueOrDefault(c.AuthorId, "")) : null, full ? c.Text : null, c.CreatedAt, full ? c.EditedAt : null,
            StateName(c.State), full && CanEdit(v, c), full && CanDelete(v, c), v.Moderates && !Gone(c, v.Now), replies);

        var replies = all.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value);
        var result = new List<CommentDto>();
        foreach (var top in all.Where(c => c.ParentId is null))
        {
            var shown = replies[top.Id].Where(Full).Select(r => Dto(r, true, [])).ToList();
            if (Full(top)) result.Add(Dto(top, true, shown));
            else if (shown.Count > 0) result.Add(Dto(top, false, shown));
        }
        return result;
    }

    public static async Task<CommentDto> OneAsync(KbDbContext db, Viewer v, Comment c, CancellationToken ct)
    {
        var name = await db.Users.Where(u => u.Id == c.AuthorId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        return new(c.Id, c.PageId, c.ParentId, new OwnerRefDto("user", c.AuthorId.ToString(), name), c.Text, c.CreatedAt, c.EditedAt, StateName(c.State),
            CanEdit(v, c), CanDelete(v, c), v.Moderates, []);
    }
}

internal sealed class ListCommentsHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time) : IActionHandler<ListComments, CommentListDto>
{
    private Page? _page;

    // Guests get 401 even where they could read the page (question Q-0027); a page the caller cannot read is 404.
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListComments> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized
        : (_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is null ? Problem.NotFound : null;

    public async Task<Outcome<CommentListDto>> HandleAsync(ActionContext<ListComments> ctx)
    {
        var (limit, offset) = (ctx.Action.Limit ?? 50, ctx.Action.Offset ?? 0);
        if (limit is < 1 or > 200) return Problem.Invalid("limit", "1-200.");
        if (offset < 0) return Problem.Invalid("offset", "0 or more.");
        var page = _page!;
        var v = await CommentRules.ViewerAsync(db, permissions, ctx.Actor, page, time, ctx.Cancel);
        var canComment = v.Enabled && v.Session && page.Kind == PageKind.Page;
        // Comments off: kept, but shown only to the space's Supervisors and Owners (comments.md, Switching comments on and off).
        IReadOnlyList<CommentDto> top = v.Enabled || v.Moderates ? await CommentRules.TopLevelAsync(db, v, page.Id, ctx.Cancel) : [];
        return new CommentListDto(v.Enabled, canComment, v.Moderates, top.Count, limit, offset, [.. top.Skip(offset).Take(limit)]);
    }
}

internal sealed class CreateCommentHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time, CommentThrottle throttle)
    : IActionHandler<CreateComment, CommentDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateComment> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized
        : (_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is null ? Problem.NotFound : null;

    public async Task<Outcome<CommentDto>> HandleAsync(ActionContext<CreateComment> ctx)
    {
        var (a, page) = (ctx.Action, _page!);
        if (page.Kind != PageKind.Page) return Problem.Refused("Comments are on pages, not folders.");
        var v = await CommentRules.ViewerAsync(db, permissions, ctx.Actor, page, time, ctx.Cancel);
        if (!v.Enabled) return Problem.Refused("Comments are off for this page.");
        if (CommentRules.Clean(a.Text) is not { } text) return CommentRules.BadText;
        if (a.ParentId is { } parentId)
        {
            // One level of replies, to a visible top-level comment on the same page (question Q-0022).
            if (!await db.Comments.AnyAsync(c => c.Id == parentId && c.PageId == page.Id && c.ParentId == null && c.State == CommentState.Visible && !c.DeletedByAuthor, ctx.Cancel))
                return Problem.Invalid("parentId", "Reply to a visible top-level comment on this page.");
            if (await db.Comments.CountAsync(c => c.ParentId == parentId, ctx.Cancel) >= CommentRules.MaxReplies)
                return Problem.Refused($"A comment takes at most {CommentRules.MaxReplies} replies.");
        }
        if (throttle.Take(v.User!.Value) is { } wait) return Problem.TooMany("Too many comments. Wait a minute.", wait);
        var c = new Comment { PageId = page.Id, ParentId = a.ParentId, AuthorId = v.User.Value, Text = text, CreatedAt = v.Now, State = CommentState.Visible };
        db.Comments.Add(c);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.CommentCreate, "comment", c.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["pageId"] = page.Id.ToString() });
        return await CommentRules.OneAsync(db, v, c, ctx.Cancel);
    }
}

internal sealed class EditCommentHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time) : IActionHandler<EditComment, CommentDto>
{
    private (Comment Comment, Page Page) _found;

    public async Task<Problem?> AuthorizeAsync(ActionContext<EditComment> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if (await CommentRules.FindAsync(db, access, ctx.Actor, ctx.Action.CommentId, track: true, ctx.Cancel) is not { } found || CommentRules.Gone(found.Comment, time.GetUtcNow())) return Problem.NotFound;
        _found = found;
        // Only the author edits; moderators hide or delete instead (question Q-0023).
        return found.Comment.AuthorId == ctx.Actor.UserId ? null : Problem.Forbidden;
    }

    public async Task<Outcome<CommentDto>> HandleAsync(ActionContext<EditComment> ctx)
    {
        var (c, page) = _found;
        var v = await CommentRules.ViewerAsync(db, permissions, ctx.Actor, page, time, ctx.Cancel);
        if (c.State != CommentState.Visible) return Problem.Refused("Only a visible comment can be edited.");
        if (!v.Enabled) return Problem.Refused("Comments are off for this page.");
        if (v.Now - c.CreatedAt > CommentRules.EditWindow) return Problem.Refused("A comment can be edited for 15 minutes after it was posted.");
        if (CommentRules.Clean(ctx.Action.Text) is not { } text) return CommentRules.BadText;
        (c.Text, c.EditedAt) = (text, v.Now);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.CommentEdit, "comment", c.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["pageId"] = page.Id.ToString() });
        return await CommentRules.OneAsync(db, v, c, ctx.Cancel);
    }
}

/// <summary>Finds the comment for a moderation call: 404 when it or its page is not there for the caller, or it is gone.</summary>
internal abstract class CommentModerationHandler<TAction>(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time) : IActionHandler<TAction, Unit>
    where TAction : IAction<Unit>
{
    protected KbDbContext Db => db;
    protected (Comment Comment, Page Page) Found { get; private set; }
    protected DateTimeOffset Now { get; private set; }
    protected bool Moderates { get; private set; }

    protected abstract Guid CommentId(TAction action);

    /// <summary>Who may go on once the comment is found: by default only the space's Supervisors and Owners (question Q-0024).</summary>
    protected virtual bool Allowed(Actor actor) => Moderates;

    public async Task<Problem?> AuthorizeAsync(ActionContext<TAction> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        Now = time.GetUtcNow();
        if (await CommentRules.FindAsync(db, access, ctx.Actor, CommentId(ctx.Action), track: true, ctx.Cancel) is not { } found || CommentRules.Gone(found.Comment, Now)) return Problem.NotFound;
        Found = found;
        Moderates = await Feedback.ModeratesAsync(permissions, ctx.Actor, found.Page.SpaceId, ctx.Cancel);
        return Allowed(ctx.Actor) ? null : Problem.Forbidden;
    }

    public abstract Task<Outcome<Unit>> HandleAsync(ActionContext<TAction> ctx);

    protected void Audit(ActionContext<TAction> ctx, string action, string? by = null)
    {
        var data = new Dictionary<string, string> { ["pageId"] = Found.Page.Id.ToString() };
        if (by is not null) data["by"] = by;
        ctx.Audit(action, "comment", Found.Comment.Id.ToString(), Found.Page.SpaceId, data);
    }
}

internal sealed class DeleteCommentHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time)
    : CommentModerationHandler<DeleteComment>(db, access, permissions, time)
{
    protected override Guid CommentId(DeleteComment action) => action.CommentId;

    protected override bool Allowed(Actor actor) => Found.Comment.AuthorId == actor.UserId || Moderates;

    /// <summary>
    /// The author deletes, at any time, also while comments are off: the text goes at once and for good, and nothing restores it (question Q-0024).
    /// With replies a placeholder stays so they keep their place; otherwise nothing is left. A moderator who deletes their own comment deletes as its author.
    /// A moderator deleting someone else's comment marks it deleted: only moderators see it, it can be restored, and it is purged 30 days later.
    /// </summary>
    public override async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteComment> ctx)
    {
        var c = Found.Comment;
        if (c.AuthorId == ctx.Actor.UserId)
        {
            if (await Db.Comments.AnyAsync(r => r.ParentId == c.Id, ctx.Cancel)) (c.Text, c.EditedAt, c.State, c.DeletedByAuthor) = (null, null, CommentState.Deleted, true);
            else Db.Comments.Remove(c);
            await Db.SaveChangesAsync(ctx.Cancel);
            Audit(ctx, AuditActions.CommentDelete, "author");
            return new Unit();
        }
        if (c.State == CommentState.Deleted) return new Unit(); // already deleted: nothing changes
        (c.State, c.ModeratedAt, c.ModeratedBy) = (CommentState.Deleted, Now, ctx.Actor.UserId);
        await Db.SaveChangesAsync(ctx.Cancel);
        Audit(ctx, AuditActions.CommentDelete, "moderator");
        return new Unit();
    }
}

internal sealed class HideCommentHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time)
    : CommentModerationHandler<HideComment>(db, access, permissions, time)
{
    protected override Guid CommentId(HideComment action) => action.CommentId;

    /// <summary>Only moderators and its author see it from then on; reversible at any time. Hiding a hidden comment changes nothing.</summary>
    public override async Task<Outcome<Unit>> HandleAsync(ActionContext<HideComment> ctx)
    {
        var c = Found.Comment;
        if (c.State == CommentState.Hidden) return new Unit();
        // shortcut: comments.md does not say what hiding a deleted comment does; it is refused, restore it first.
        if (c.State == CommentState.Deleted) return Problem.Conflict("The comment is deleted. Restore it first.");
        (c.State, c.ModeratedAt, c.ModeratedBy) = (CommentState.Hidden, Now, ctx.Actor.UserId);
        await Db.SaveChangesAsync(ctx.Cancel);
        Audit(ctx, AuditActions.CommentHide);
        return new Unit();
    }
}

internal sealed class RestoreCommentHandler(KbDbContext db, IAccess access, SpacePermissionService permissions, TimeProvider time)
    : CommentModerationHandler<RestoreComment>(db, access, permissions, time)
{
    protected override Guid CommentId(RestoreComment action) => action.CommentId;

    /// <summary>A hidden comment, or one a moderator deleted and not yet purged, becomes visible again; a visible one is 409.</summary>
    public override async Task<Outcome<Unit>> HandleAsync(ActionContext<RestoreComment> ctx)
    {
        var c = Found.Comment;
        if (c.State == CommentState.Visible) return Problem.Conflict("The comment is visible.");
        (c.State, c.ModeratedAt, c.ModeratedBy) = (CommentState.Visible, null, null);
        await Db.SaveChangesAsync(ctx.Cancel);
        Audit(ctx, AuditActions.CommentRestore);
        return new Unit();
    }
}

/// <summary>Purges what moderators deleted 30 days ago or more: a placeholder without text stays while the comment has replies.</summary>
public static class CommentPurge
{
    public static async Task<int> RunAsync(IServiceProvider services, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
        var before = now - CommentRules.PurgeAfter;
        var due = await db.Comments.Where(c => c.State == CommentState.Deleted && !c.DeletedByAuthor && c.ModeratedAt <= before).ToListAsync(ct);
        foreach (var c in due)
        {
            if (await db.Comments.AnyAsync(r => r.ParentId == c.Id, ct)) (c.Text, c.EditedAt, c.DeletedByAuthor) = (null, null, true);
            else db.Comments.Remove(c);
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}

public sealed class CommentPurger(IServiceProvider services, TimeProvider time, ILogger<CommentPurger> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var n = await CommentPurge.RunAsync(services, time.GetUtcNow(), stoppingToken);
                if (n > 0) log.LogInformation("Purged {Count} deleted comments.", n);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Comment purge failed."); }
        }
    }
}
