using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The storage routes go through the action bus: each answers with its action's name, and only an Admin gets past the handler.
/// Storage is core only: no plugin can hook it, so there is no probe here. StorageTests and StorageMoveTests pin the answers.
/// </summary>
public sealed class StoragePipelineTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/storage";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"kb-test-{Guid.NewGuid():N}.place");
    // The background copy is taken out, as in StorageMoveTests: a started move stays "Copying", so every answer below is fixed and not a race.
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath(), services: s =>
    {
        foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null).ToList()) s.Remove(d);
    });
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    /// <summary>Every route of the slice, sent well formed, with the name of its action. <paramref name="place"/>: a place that is not there, for a caller who may add none.</summary>
    private async Task<List<(string Name, HttpResponseMessage Answer)>> AllAsync(HttpClient c, Guid? place = null)
    {
        var read = await c.GetAsync(Url, Ct);
        var add = await c.PostAsJsonAsync($"{Url}/places", new { name = "Second", kind = "folder", settings = new { path = _folder } }, Ct);
        // Read as text: the test reads this answer again.
        var id = place ?? JsonSerializer.Deserialize<JsonElement>(await add.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid();
        return
        [
            ("storage.read", read),
            ("storage.place.add", add),
            ("storage.place.save", await c.PutAsJsonAsync($"{Url}/places/{id}", new { name = "Second 2", settings = new { path = _folder } }, Ct)),
            ("storage.place.test", await c.PostAsync($"{Url}/places/{id}/test", null, Ct)),
            ("storage.move.start", await c.PostAsJsonAsync($"{Url}/move", new { toId = id }, Ct)),
            ("storage.move.copy-again", await c.PostAsync($"{Url}/move/copy-again", null, Ct)),
            ("storage.move.preview", await c.PutAsJsonAsync($"{Url}/move/preview", new { on = true }, Ct)),
            ("storage.move.switch", await c.PostAsync($"{Url}/move/switch", null, Ct)),
            ("storage.move.cancel", await c.PostAsync($"{Url}/move/cancel", null, Ct)),
            ("storage.place.remove", await c.DeleteAsync($"{Url}/places/{id}", Ct)),
        ];
    }

    [Fact]
    public async Task EveryStorageRoute_AnswersWithItsActionName()
    {
        var answers = await AllAsync(await _f.SignedInBreakGlassAsync());

        Assert.All(answers, a => Assert.Equal(a.Name, Action(a.Answer)));
        // The answers are what they were. A refusal by the core (409: nothing is copied yet, so no preview and no switch) names the action too.
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent,
             HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.NoContent, HttpStatusCode.NoContent],
            answers.Select(a => a.Answer.StatusCode));
        Assert.Equal(["kinds", "places", "move"], (await answers[0].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().Select(p => p.Name));
        var added = JsonSerializer.Deserialize<JsonElement>(await answers[1].Answer.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["id", "name", "kind", "settings", "secretsSet", "role", "createdAt"], added.EnumerateObject().Select(p => p.Name));
        Assert.Equal($"{Url}/places/{added.GetProperty("id").GetString()}", answers[1].Answer.Headers.Location?.OriginalString);
        Assert.Equal("Second 2", (await answers[2].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());
        Assert.Equal("""{"ok":true,"error":null}""", await answers[3].Answer.Content.ReadAsStringAsync(Ct));
        var move = await answers[4].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["id", "fromId", "toId", "state", "total", "copied", "checked", "failed", "lastError", "preview", "startedAt", "finishedAt"], move.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Url, answers[4].Answer.Headers.Location?.OriginalString);
        Assert.Equal("""{"error":"Not everything was copied."}""", await answers[6].Answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task EveryStorageRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        _ = await _f.SignedInBreakGlassAsync(); // the app is set up
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "u1", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = "u1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();

        var answers = await AllAsync(c, place: Guid.NewGuid());

        // The handler refuses, not a filter in front of it: the answer names the action. 403 comes before 404: the place is not there.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.StoragePlaces.AnyAsync(p => p.Name == "Second", Ct));
            Assert.False(await db.StorageMoves.AnyAsync(Ct));
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action.StartsWith("storage."), Ct));
        });
    }
}
