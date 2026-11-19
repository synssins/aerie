using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.DataCenter;

/// <summary>
/// The surface gives a page, a space and a version their row the first time it meets them. Several requests that meet the same
/// object at once must end with one row, the same id in every answer, and nothing in the log: that is normal under load, not a fault.
/// </summary>
public sealed class DcFirstUseTests
{
    /// <summary>What the app logs as an error or worse.</summary>
    private sealed class Errors : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);
        public void Dispose() { }
        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error) lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception?.Message}");
            }
        }
    }

    [Fact]
    public async Task ManyRequestsThatMeetTheSameObjectAtOnce_MakeOneRow_AndLogNoError()
    {
        var errors = new Errors();
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath(), services: s => s.AddSingleton<ILoggerProvider>(errors));
        var admin = await f.SignedInBreakGlassAsync();
        var reader = await UserAsync(f, "rita");
        var rita = Basic(f, "rita");
        const string doc = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hi"}]}]}""";

        for (var round = 0; round < 4; round++)
        {
            // Made through Aerie's own API: neither the space nor the page has met the surface yet.
            var key = "race" + round;
            var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Race " + round, key }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
            (await admin.PutAsJsonAsync($"/api/v1/spaces/{space}/roles", new { principalType = "User", principalId = reader.ToString(), role = "Reader" }, Ct)).EnsureSuccessStatusCode();
            var page = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Native " + round, contentJson = doc }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();

            var answers = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => i % 4 == 3
                ? rita.GetAsync($"/rest/api/space/{key.ToUpperInvariant()}", Ct)
                : rita.GetAsync($"/rest/api/content/{page}?expand=version,space", Ct)));

            var pageIds = new HashSet<string>();
            var spaceIds = new HashSet<long>();
            foreach (var r in answers)
            {
                var json = await r.JsonAsync(HttpStatusCode.OK);
                if (json.TryGetProperty("space", out var s)) { pageIds.Add(json.GetProperty("id").GetString()!); spaceIds.Add(s.GetProperty("id").GetInt64()); }
                else spaceIds.Add(json.GetProperty("id").GetInt64());
            }
            Assert.Single(pageIds);
            Assert.Single(spaceIds);
            Assert.Equal(1, await f.WithDbValueAsync(db => db.DcContents.CountAsync(d => d.AerieId == page, Ct)));
            Assert.Equal(1, await f.WithDbValueAsync(db => db.DcSpaces.CountAsync(d => d.SpaceId == space, Ct)));
            Assert.Equal(1, await f.WithDbValueAsync(db => db.DcVersions.CountAsync(d => d.PageId == page, Ct)));
        }
        Assert.Empty(errors.Lines);
    }
}
