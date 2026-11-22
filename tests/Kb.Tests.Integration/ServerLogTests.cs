using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Kb.Data;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kb.Tests.Integration;

/// <summary>
/// Administration &gt; Server log: the server's own log, kept in the database. Warnings and errors of every category and the information
/// lines of the app's own (Kb.*) are kept, newest first, filtered by level and text, read by Admins only, through a restart, and pruned.
/// </summary>
public sealed class ServerLogTests : IAsyncLifetime
{
    private const string Password = "user-password-0123456789";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<List<JsonElement>> ListAsync(string query) =>
        [.. (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/server-log?" + query, Ct)).EnumerateArray()];

    /// <summary>The writer works in the background: waits until the lines with this mark are in.</summary>
    private async Task<List<JsonElement>> WaitForAsync(string mark, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            var rows = await ListAsync("take=500&text=" + Uri.EscapeDataString(mark));
            if (rows.Count >= count) return rows;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"Fewer than {count} lines with {mark} were kept.");
    }

    private void Write(string category, LogLevel level, string message, Exception? e = null) =>
        _f.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category).Log(level, e, "{Message}", message);

    [Fact]
    public async Task KeepsWarningsOfAll_AndInformationOfTheAppsOwn_NewestFirst()
    {
        var mark = Guid.NewGuid().ToString("N");
        Write("Kb.Web.Test", LogLevel.Information, $"one {mark}");
        Write("Other.Library", LogLevel.Information, $"not kept {mark}");
        Write("Kb.Web.Test", LogLevel.Debug, $"not kept {mark}");
        Write("Other.Library", LogLevel.Warning, $"two {mark}");
        Write("Kb.Web.Test", LogLevel.Error, $"three {mark}", new InvalidOperationException("It broke."));

        var rows = await WaitForAsync(mark, 3);
        await Task.Delay(200, Ct); // and nothing more arrives
        rows = await ListAsync("take=500&text=" + mark);
        Assert.Equal(["three", "two", "one"], rows.Select(r => r.GetProperty("message").GetString()!.Split(' ')[0]));
        Assert.Equal(["Error", "Warning", "Information"], rows.Select(r => r.GetProperty("level").GetString()));
        Assert.Equal("Other.Library", rows[1].GetProperty("category").GetString());
        Assert.Contains("InvalidOperationException: It broke.", rows[0].GetProperty("exception").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("exception").ValueKind);
    }

    [Fact]
    public async Task FiltersByLevelAndText_AndPages()
    {
        var mark = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 5; i++) Write("Kb.Web.Test", LogLevel.Information, $"info {i} {mark}");
        Write("Kb.Web.Test", LogLevel.Warning, $"warn {mark}");
        Write("Kb.Web.Test", LogLevel.Critical, $"down {mark.ToUpperInvariant()}");
        await WaitForAsync(mark, 7);

        Assert.Equal(2, (await ListAsync($"level=warning&text={mark}")).Count);
        Assert.Single(await ListAsync($"level=Critical&text={mark}"));
        Assert.Single(await ListAsync($"text=warn%20{mark}")); // text matches any case, and the category too
        Assert.Empty(await ListAsync($"text={mark}%25")); // % is matched as itself
        var first = await ListAsync($"take=3&text={mark}");
        var second = await ListAsync($"take=3&offset=3&text={mark}");
        Assert.Equal(3, first.Count);
        Assert.True(first[^1].GetProperty("id").GetInt64() > second[0].GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/api/v1/admin/server-log?level=Debug", Ct)).StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdmin_ReadsIt()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var user = new KbUser { UserName = "dana", DisplayName = "dana", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>().CreateAsync(user, Password)).Succeeded);
        }
        var dana = _f.NewClient();
        (await dana.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "dana", password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await dana.GetAsync("/api/v1/admin/server-log", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("/api/v1/admin/server-log", Ct)).StatusCode);
    }

    [Fact]
    public async Task Survives_ARestart()
    {
        var mark = Guid.NewGuid().ToString("N");
        Write("Kb.Web.Test", LogLevel.Warning, $"before {mark}");
        await WaitForAsync(mark, 1);
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        Assert.Single(await ListAsync($"text={mark}"));
    }

    [Fact]
    public async Task OldLines_AndThoseBeyondTheLimit_AreRemoved()
    {
        var now = DateTimeOffset.UtcNow;
        await _f.WithDbAsync(async db =>
        {
            await db.ServerLogEntries.ExecuteDeleteAsync(Ct);
            db.ServerLogEntries.Add(new ServerLogEntry { At = now.AddDays(-31), Level = 3, Category = "Kb.Old", Message = "old" });
            for (var i = 0; i < 4; i++) db.ServerLogEntries.Add(new ServerLogEntry { At = now.AddMinutes(i), Level = 3, Category = "Kb.New", Message = $"new {i}" });
            await db.SaveChangesAsync(Ct);

            Assert.Equal(2, await ServerLogWriter.PruneAsync(db, now, Ct, days: 30, max: 3));
            Assert.Equal(["new 1", "new 2", "new 3"], await db.ServerLogEntries.OrderBy(e => e.Id).Select(e => e.Message).ToListAsync(Ct));
        });
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdef0123456789xyz", "abcdef0123456789xyz")]
    [InlineData("Server=db;User Id=sa;Password=Hunter2Hunter2;", "Hunter2Hunter2")]
    [InlineData("""{"apiKey":"my-model-key-123"}""", "my-model-key-123")]
    [InlineData("GET https://ai.example/v1?key=abc123&x=1", "abc123")]
    [InlineData("Incorrect API key provided: sk-abcdefghijklmnopqrstu", "sk-abcdefghijklmnopqrstu")]
    [InlineData("token kbt_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123456789_abcdef", "kbt_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef")]
    public void ASecret_IsHidden_BeforeALineIsKept(string line, string secret)
    {
        var kept = ServerLog.Redact(line);
        Assert.DoesNotContain(secret, kept, StringComparison.Ordinal);
        Assert.Contains("[hidden]", kept, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Import item 3 drafted in 812 ms: Done.")]
    [InlineData("The model server answered 500 for import after 90 ms: status code: 500")]
    [InlineData("Import tools for 4: 2 model call(s), 3 tool call(s), 100+20 tokens, 9 ms.")]
    public void AnOrdinaryLine_IsKeptAsItIs(string line) => Assert.Equal(line, ServerLog.Redact(line));
}
