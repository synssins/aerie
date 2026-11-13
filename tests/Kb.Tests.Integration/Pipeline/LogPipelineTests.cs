using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The log shipping routes go through the action bus: each answers with its action's name, and only an Admin gets past the handler.
/// These actions are core only: no plugin can hook them, so there is nothing more to test here. LogShippingTests pins the answers.
/// </summary>
public sealed class LogPipelineTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/logs";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private static object Target(string name) => new { name, kind = "file", settings = new { path = Path.Combine(Path.GetTempPath(), $"kb-test-{Guid.NewGuid():N}.logs") }, enabled = false };

    [Fact]
    public async Task EveryLogRoute_AnswersWithItsActionName()
    {
        var admin = await _f.SignedInBreakGlassAsync();

        var read = await admin.GetAsync(Url, Ct);
        Assert.Equal((HttpStatusCode.OK, "log.read"), (read.StatusCode, Action(read)));
        Assert.Equal(["kinds", "targets", "head"], (await read.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().Select(p => p.Name));

        var added = await admin.PostAsJsonAsync($"{Url}/targets", Target("Second file"), Ct);
        Assert.Equal((HttpStatusCode.Created, "log.target.add"), (added.StatusCode, Action(added)));
        var target = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = target.GetProperty("id").GetString();
        Assert.Equal($"{Url}/targets/{id}", added.Headers.Location?.ToString());
        Assert.Equal(["id", "name", "kind", "settings", "secretsSet", "enabled", "lastSequence", "lastShippedAt", "lastError", "lastErrorAt", "createdAt"],
            target.EnumerateObject().Select(p => p.Name));

        var saved = await admin.PutAsJsonAsync($"{Url}/targets/{id}", Target("Renamed"), Ct);
        Assert.Equal((HttpStatusCode.OK, "log.target.save"), (saved.StatusCode, Action(saved)));
        Assert.Equal("Renamed", (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());

        var tested = await admin.PostAsync($"{Url}/targets/{id}/test", null, Ct);
        Assert.Equal((HttpStatusCode.OK, "log.target.test"), (tested.StatusCode, Action(tested)));
        Assert.Equal(["ok", "error"], (await tested.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().Select(p => p.Name));

        var removed = await admin.DeleteAsync($"{Url}/targets/{id}", Ct);
        Assert.Equal((HttpStatusCode.NoContent, "log.target.remove"), (removed.StatusCode, Action(removed)));

        // A refusal by the core names the action too.
        var invalid = await admin.PostAsJsonAsync($"{Url}/targets", new { name = "x", kind = "pigeon", settings = new { } }, Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "log.target.add"), (invalid.StatusCode, Action(invalid)));
        var missing = new[]
        {
            ("log.target.save", await admin.PutAsJsonAsync($"{Url}/targets/{id}", Target("Gone"), Ct)),
            ("log.target.test", await admin.PostAsync($"{Url}/targets/{id}/test", null, Ct)),
            ("log.target.remove", await admin.DeleteAsync($"{Url}/targets/{id}", Ct)),
        };
        Assert.All(missing, m => Assert.Equal((HttpStatusCode.NotFound, m.Item1), (m.Item2.StatusCode, Action(m.Item2))));
    }

    [Fact]
    public async Task EveryLogRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        var id = (await (await admin.PostAsJsonAsync($"{Url}/targets", Target("Kept"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString();
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "u1", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var user = _f.NewClient();
        (await user.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "u1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        var answers = new[]
        {
            ("log.read", await user.GetAsync(Url, Ct)),
            ("log.target.add", await user.PostAsJsonAsync($"{Url}/targets", Target("Not mine"), Ct)),
            ("log.target.save", await user.PutAsJsonAsync($"{Url}/targets/{id}", Target("Changed"), Ct)),
            ("log.target.test", await user.PostAsync($"{Url}/targets/{id}/test", null, Ct)),
            ("log.target.remove", await user.DeleteAsync($"{Url}/targets/{id}", Ct)),
        };

        // The handler refuses, not a filter in front of it: the answer names the action. An id that exists is not told apart from one that does not.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Item1), (a.Item2.StatusCode, Action(a.Item2))));
        var gone = await user.DeleteAsync($"{Url}/targets/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, gone.StatusCode);
        await _f.WithDbAsync(async db => Assert.Equal(["Kept", "Local log file"], await db.LogTargets.Select(t => t.Name).OrderBy(n => n).ToListAsync(Ct)));
    }
}
