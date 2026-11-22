using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Administration > Server log. Core only: not in the contract assembly, so no plugin can read or hook the server's own log.

/// <summary>Level: Information, Warning, Error or Critical.</summary>
public sealed record ServerLogEntryDto(long Id, DateTimeOffset At, string Level, string Category, string Message, string? Exception);

/// <summary>The newest lines of the server's own log first. Admins only.</summary>
[Action("serverlog.list", Read = true)]
public sealed record ListServerLog : IAction<IReadOnlyList<ServerLogEntryDto>>
{
    /// <summary>How many: 1 to 500. Null is 100.</summary>
    public int? Take { get; init; }
    /// <summary>How many of the newest to pass over first: the next page.</summary>
    public int? Offset { get; init; }
    /// <summary>Only this level and those above it: Information, Warning, Error or Critical.</summary>
    public string? Level { get; init; }
    /// <summary>Only lines whose message, category or exception holds this text (any case).</summary>
    public string? Text { get; init; }
}

internal sealed class ListServerLogHandler(KbDbContext db, IAccess access) : IActionHandler<ListServerLog, IReadOnlyList<ServerLogEntryDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListServerLog> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<ServerLogEntryDto>>> HandleAsync(ActionContext<ListServerLog> ctx)
    {
        var req = ctx.Action;
        var q = db.ServerLogEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(req.Level))
        {
            if (!Enum.TryParse<LogLevel>(req.Level.Trim(), true, out var level) || level is < LogLevel.Information or >= LogLevel.None)
                return Problem.Invalid("level", "Information, Warning, Error or Critical.");
            var min = (int)level;
            q = q.Where(e => e.Level >= min);
        }
        if (req.Text?.Trim() is { Length: > 0 } text)
        {
            if (text.Length > 200) return Problem.Invalid("text", "At most 200 characters.");
            // LIKE is without case on both providers; the text's own % and _ are matched as themselves.
            var like = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            q = q.Where(e => EF.Functions.Like(e.Message, like, "\\") || EF.Functions.Like(e.Category, like, "\\") || (e.Exception != null && EF.Functions.Like(e.Exception, like, "\\")));
        }
        var rows = await q.OrderByDescending(e => e.Id).Skip(Math.Max(req.Offset ?? 0, 0)).Take(Math.Clamp(req.Take ?? 100, 1, 500)).ToListAsync(ctx.Cancel);
        return Outcome.List<ServerLogEntryDto>([.. rows.Select(e => new ServerLogEntryDto(e.Id, e.At, ((LogLevel)e.Level).ToString(), e.Category, e.Message, e.Exception))]);
    }
}
