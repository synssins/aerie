using System.Text.RegularExpressions;
using System.Threading.Channels;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// Keeps the server's own log in the database, so an Admin reads it in Administration &gt; Server log without access to the host.
/// Kept: warnings and errors of every category, and the information lines of the app's own categories (Kb.*). The console log is unchanged.
/// A line is queued here and written by <see cref="ServerLogWriter"/>; logging never waits for the database.
/// </summary>
public sealed partial class ServerLog : ILoggerProvider
{
    public const int MessageMax = 4000, ExceptionMax = 32_000, CategoryMax = 300;

    // shortcut: when more than this many lines wait to be written (a storm of errors), new ones are dropped from the kept log; the console still has them.
    private readonly Channel<ServerLogEntry> _queue = Channel.CreateBounded<ServerLogEntry>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>True on the writer's own flow: what it and the database say while writing is not kept, or a failing write would feed itself.</summary>
    internal static readonly AsyncLocal<bool> Quiet = new();

    internal ChannelReader<ServerLogEntry> Reader => _queue.Reader;

    public static bool Keeps(string category, LogLevel level) =>
        level is >= LogLevel.Warning and < LogLevel.None || level == LogLevel.Information && category.StartsWith("Kb.", StringComparison.Ordinal);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() => _queue.Writer.TryComplete();

    internal void Add(string category, LogLevel level, string message, Exception? exception) =>
        _queue.Writer.TryWrite(new ServerLogEntry
        {
            At = DateTimeOffset.UtcNow,
            Level = (int)level,
            Category = Cut(category, CategoryMax),
            Message = Cut(Redact(message), MessageMax),
            Exception = exception is null ? null : Cut(Redact(exception.ToString()), ExceptionMax),
        });

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// A guard, not the rule: no line of the app is written with a secret in it. Should one ever carry one (an exception's text, a
    /// server's answer), what looks like a password, key or token is replaced before the line is kept.
    /// </summary>
    public static string Redact(string text)
    {
        text = Bearer().Replace(text, "${1}[hidden]");
        text = Named().Replace(text, "${1}${2}[hidden]");
        text = KnownToken().Replace(text, "[hidden]");
        return text;
    }

    [GeneratedRegex(@"\b(Bearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // password=..., "apiKey": "...", client_secret=..., ?token=..., &code=... and the like.
    [GeneratedRegex(@"((?:""?\b(?:password|passwd|pwd|secret|client[_-]?secret|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|authorization)\b""?)|(?:[?&](?:code|key|sig|signature)))(\s*[=:]\s*""?)(?!\[hidden\])[^\s"";,&]+", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    // The app's own API tokens (kbt_...) and the common form of model server keys (sk-...).
    [GeneratedRegex(@"\b(?:kbt_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{16,})")]
    private static partial Regex KnownToken();

    private sealed class Logger(ServerLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => !Quiet.Value && Keeps(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel)) owner.Add(category, logLevel, formatter(state, exception), exception);
        }
    }
}

/// <summary>Writes the queued lines of <see cref="ServerLog"/> in batches, and once an hour removes those older than <see cref="Days"/> or beyond <see cref="MaxEntries"/>.</summary>
public sealed class ServerLogWriter(ServerLog log, IServiceScopeFactory scopes, TimeProvider time, ILogger<ServerLogWriter> logger) : BackgroundService
{
    public const int Days = 30, MaxEntries = 50_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServerLog.Quiet.Value = true;
        var nextPrune = DateTimeOffset.MinValue;
        var batch = new List<ServerLogEntry>();
        while (await log.Reader.WaitToReadAsync(stoppingToken))
        {
            while (batch.Count < 500 && log.Reader.TryRead(out var e)) batch.Add(e);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<KbDbContext>();
                db.ServerLogEntries.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);
                var now = time.GetUtcNow();
                if (now >= nextPrune) { await PruneAsync(db, now, stoppingToken); nextPrune = now.AddHours(1); }
            }
            // To the console only (Quiet): the lines of this batch are lost from the kept log.
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Writing the server log to the database failed."); }
            batch.Clear();
        }
    }

    /// <summary>Removes lines older than <paramref name="days"/>, then all but the newest <paramref name="max"/>.</summary>
    public static async Task<int> PruneAsync(KbDbContext db, DateTimeOffset now, CancellationToken ct, int days = Days, int max = MaxEntries)
    {
        ArgumentNullException.ThrowIfNull(db);
        var oldest = now.AddDays(-days);
        var n = await db.ServerLogEntries.Where(e => e.At < oldest).ExecuteDeleteAsync(ct);
        if (await db.ServerLogEntries.OrderByDescending(e => e.Id).Skip(max).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct) is { } cut)
            n += await db.ServerLogEntries.Where(e => e.Id <= cut).ExecuteDeleteAsync(ct);
        return n;
    }
}
