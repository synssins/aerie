using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// The API token routes go through the action bus: each answers with its action's name, and only an Admin gets past the handlers of /admin/tokens.
/// Their actions are core only, so no plugin can hook them: there is no refusal by a plugin to test.
/// </summary>
public sealed class TokenPipelineTests : IAsyncLifetime
{
    private const string Api = "/api/v1";
    private readonly KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;

    private static Task<HttpResponseMessage> CreateAsync(HttpClient c, string kind = "Personal") =>
        c.PostAsJsonAsync($"{Api}/tokens", new { name = "Agent", kind, scopes = new[] { new { spaceId = (Guid?)null, access = "Read" } }, days = 30 }, Ct);

    /// <summary>The three routes under /admin/tokens, sent well formed, with the name of their action.</summary>
    private static async Task<List<(string Name, HttpResponseMessage Answer)>> AdminRoutesAsync(HttpClient c) =>
    [
        ("token.list.all", await c.GetAsync($"{Api}/admin/tokens", Ct)),
        ("token.settings.read", await c.GetAsync($"{Api}/admin/tokens/settings", Ct)),
        ("token.settings.save", await c.PutAsJsonAsync($"{Api}/admin/tokens/settings", new { maxDays = 90 }, Ct)),
    ];

    private async Task<HttpClient> UserAsync()
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = "u1", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync($"{Api}/auth/local/signin", new { userName = "u1", password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task EveryTokenRoute_AnswersWithItsActionName()
    {
        var created = await CreateAsync(_admin);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("info").GetProperty("id").GetString()!;
        var text = body.GetProperty("token").GetString()!;
        var list = await _admin.GetAsync($"{Api}/tokens", Ct);
        var admin = await AdminRoutesAsync(_admin);
        var revoked = await _admin.DeleteAsync($"{Api}/tokens/{id}", Ct);
        List<(string Name, HttpResponseMessage Answer)> answers = [("token.create", created), ("token.list", list), .. admin, ("token.revoke", revoked)];

        Assert.All(answers, a => Assert.Equal(a.Name, Action(a.Answer)));
        // The answers are what they were.
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent], answers.Select(a => a.Answer.StatusCode));
        Assert.Equal($"{Api}/tokens/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal(["token", "info"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "name", "kind", "prefix", "scopes", "ownerId", "owner", "createdAt", "expiresAt", "revokedAt", "lastUsedAt", "state"],
            body.GetProperty("info").EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Agent", "Personal", "Active"), (body.GetProperty("info").GetProperty("name").GetString(), body.GetProperty("info").GetProperty("kind").GetString(), body.GetProperty("info").GetProperty("state").GetString()));
        Assert.Equal(id, (await list.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal(id, (await admin[0].Answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal("""{"maxDays":365}""", await admin[1].Answer.Content.ReadAsStringAsync(Ct));
        Assert.Equal("""{"maxDays":90}""", await admin[2].Answer.Content.ReadAsStringAsync(Ct));

        // Each write is audited once, as the caller, with the data it always had. The token's text is in no entry.
        await _f.WithDbAsync(async db =>
        {
            var me = (await db.Users.SingleAsync(u => u.IsBreakGlass, Ct)).Id;
            var entries = await db.AuditEntries.Where(a => a.Action == AuditActions.TokenCreate || a.Action == AuditActions.TokenRevoke || a.Action == AuditActions.SettingsChange)
                .OrderBy(a => a.Sequence).ToListAsync(Ct);
            Assert.Equal([(AuditActions.TokenCreate, "token", id), (AuditActions.SettingsChange, "setting", ApiTokenAuth.MaxDaysKey), (AuditActions.TokenRevoke, "token", id)],
                entries.Select(e => (e.Action, e.TargetType!, e.TargetId!)));
            Assert.All(entries, e => { Assert.Equal(me, e.ActorId); Assert.DoesNotContain(text[12..], e.DataJson, StringComparison.Ordinal); });
            Assert.Equal(new Dictionary<string, string> { ["name"] = "Agent", ["kind"] = "Personal", ["days"] = "30", ["scopes"] = "all:Read" },
                JsonSerializer.Deserialize<Dictionary<string, string>>(entries[0].DataJson));
            Assert.Equal("""{"value":"90"}""", entries[1].DataJson);
            Assert.Equal("""{"name":"Agent"}""", entries[2].DataJson);
            var stored = await db.RuntimeSettings.SingleAsync(s => s.Key == ApiTokenAuth.MaxDaysKey, Ct);
            Assert.Equal(("90", me), (stored.Value, stored.UpdatedBy));
        });
    }

    [Fact]
    public async Task EveryAdminTokenRoute_Is403_ForASignedInUserWhoIsNotAnAdmin()
    {
        var id = (await (await CreateAsync(_admin)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("info").GetProperty("id").GetString();
        var user = await UserAsync();

        var answers = await AdminRoutesAsync(user);

        // The handler refuses, not a filter in front of it: the answer names the action.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Forbidden, a.Name), (a.Answer.StatusCode, Action(a.Answer))));
        // The same person may not make a service token, and another person's token does not exist for them.
        var service = await CreateAsync(user, "Service");
        Assert.Equal((HttpStatusCode.Forbidden, "token.create"), (service.StatusCode, Action(service)));
        var revoke = await user.DeleteAsync($"{Api}/tokens/{id}", Ct);
        Assert.Equal((HttpStatusCode.NotFound, "token.revoke"), (revoke.StatusCode, Action(revoke)));
        await _f.WithDbAsync(async db =>
        {
            Assert.False(await db.RuntimeSettings.AnyAsync(s => s.Key == ApiTokenAuth.MaxDaysKey, Ct));
            var token = await db.ApiTokens.SingleAsync(Ct);
            Assert.Null(token.RevokedAt);
            Assert.False(await db.Users.AnyAsync(u => u.Source == UserSource.Service, Ct));
        });
    }
}
