using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The access and notification routes go through the action bus: each answers with its action's name, and answers what it always did.</summary>
public sealed class AccessPipelineTests : IAsyncLifetime
{
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private Guid _space;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
        _space = await SpaceAsync("sd", "Service Desk", sensitive: false);
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task<Guid> SpaceAsync(string key, string name, bool sensitive) =>
        (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key, name, isSensitive = sensitive }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    /// <summary>A signed-in local user who is not an Admin and has no role anywhere.</summary>
    private async Task<HttpClient> UserAsync(string name)
    {
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<KbUser>>();
            Assert.True((await users.CreateAsync(new KbUser { UserName = name, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow }, "user-password-0123456789")).Succeeded);
        }
        var c = _f.NewClient();
        (await c.PostAsJsonAsync("/api/v1/auth/local/signin", new { userName = name, password = "user-password-0123456789" }, Ct)).EnsureSuccessStatusCode();
        return c;
    }

    private static Task<HttpResponseMessage> RequestAsync(HttpClient c, Guid space) => c.PostAsJsonAsync($"/api/v1/spaces/{space}/access-requests", new { role = "Reader", reason = "On call this week" }, Ct);
    private static Task<HttpResponseMessage> DecideAsync(HttpClient c, Guid request, bool approve = true) => c.PostAsJsonAsync($"/api/v1/access-requests/{request}/decision", new { approve }, Ct);
    private static Task<HttpResponseMessage> EmergencyAsync(HttpClient c, Guid space, string reason) => c.PostAsJsonAsync($"/api/v1/spaces/{space}/emergency-access", new { reason }, Ct);
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
    private static async Task<Guid> IdAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("id").GetGuid();
    private static string? Action(HttpResponseMessage r) => r.Headers.TryGetValues("X-Kb-Action", out var v) ? v.Single() : null;
    private async Task<JsonElement> CountersAsync() => (await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct)).GetProperty("byAction");

    [Fact]
    public async Task EveryAccessRoute_AnswersWithItsActionName()
    {
        var requested = await RequestAsync(_admin, _space);
        Assert.Equal((HttpStatusCode.Accepted, "access.request"), (requested.StatusCode, Action(requested)));
        var body = await JsonAsync(requested);
        Assert.Equal(["id"], body.EnumerateObject().Select(p => p.Name));
        var request = body.GetProperty("id").GetGuid();
        var noSpace = await RequestAsync(_admin, Guid.NewGuid());
        Assert.Equal((HttpStatusCode.NotFound, "access.request"), (noSpace.StatusCode, Action(noSpace)));

        var decided = await DecideAsync(_admin, request);
        Assert.Equal((HttpStatusCode.NoContent, "access.decide"), (decided.StatusCode, Action(decided)));
        var again = await DecideAsync(_admin, request);
        Assert.Equal((HttpStatusCode.Conflict, "access.decide"), (again.StatusCode, Action(again)));
        Assert.Equal("Request already decided.", (await JsonAsync(again)).GetProperty("error").GetString());
        var noRequest = await DecideAsync(_admin, Guid.NewGuid());
        Assert.Equal((HttpStatusCode.NotFound, "access.decide"), (noRequest.StatusCode, Action(noRequest)));

        var hidden = await SpaceAsync("hr", "HR", sensitive: true);
        var opened = await EmergencyAsync(_admin, hidden, "SSO outage investigation");
        Assert.Equal((HttpStatusCode.OK, "space.sensitive.emergency"), (opened.StatusCode, Action(opened)));
        var grant = await JsonAsync(opened);
        Assert.Equal(["expiresAt"], grant.EnumerateObject().Select(p => p.Name));
        Assert.True(grant.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        var noReason = await EmergencyAsync(_admin, hidden, " ");
        Assert.Equal((HttpStatusCode.BadRequest, "space.sensitive.emergency"), (noReason.StatusCode, Action(noReason)));
        Assert.Equal("A reason is required. (Parameter 'reason')", (await JsonAsync(noReason)).GetProperty("errors").GetProperty("reason")[0].GetString());
        var notSensitive = await EmergencyAsync(_admin, _space, "SSO outage investigation");
        Assert.Equal((HttpStatusCode.Conflict, "space.sensitive.emergency"), (notSensitive.StatusCode, Action(notSensitive)));
        Assert.Equal("Emergency access applies only to sensitive spaces.", (await JsonAsync(notSensitive)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await EmergencyAsync(_admin, Guid.NewGuid(), "SSO outage investigation")).StatusCode);

        // The service wrote each audit entry once, as the caller; the bus added none.
        await _f.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.Action == AuditActions.AccessRequest || a.Action == AuditActions.AccessDecide || a.Action == AuditActions.EmergencyAccess).ToListAsync(Ct);
            Assert.Equal([AuditActions.AccessDecide, AuditActions.AccessRequest, AuditActions.EmergencyAccess], entries.Select(a => a.Action).Order());
            Assert.All(entries, a => Assert.NotNull(a.ActorId));
        });
    }

    [Fact]
    public async Task EveryNotificationRoute_AnswersWithItsActionName_AndTheSameJson()
    {
        // Signing in with the break-glass account notifies the Admins, this one included.
        var listed = await _admin.GetAsync("/api/v1/notifications", Ct);
        Assert.Equal((HttpStatusCode.OK, "notification.list"), (listed.StatusCode, Action(listed)));
        var all = (await JsonAsync(listed)).EnumerateArray().ToList();
        Assert.True(all.Count >= 2);
        Assert.All(all, n => Assert.Equal(["id", "userId", "kind", "title", "body", "link", "createdAt", "readAt"], n.EnumerateObject().Select(p => p.Name)));
        Assert.All(all, n => Assert.Equal(JsonValueKind.Null, n.GetProperty("readAt").ValueKind));
        Assert.Contains(all, n => n.GetProperty("title").GetString() == "Break-glass account signed in");
        Assert.Equal(all.Select(n => n.GetProperty("createdAt").GetDateTimeOffset()).OrderDescending(), all.Select(n => n.GetProperty("createdAt").GetDateTimeOffset()));
        Assert.True((await CountersAsync()).GetProperty("filter:notification.list").GetInt32() >= 1); // a read: result filters run on it

        var counted = await _admin.GetAsync("/api/v1/notifications/count", Ct);
        Assert.Equal((HttpStatusCode.OK, "notification.count"), (counted.StatusCode, Action(counted)));
        Assert.Equal($$"""{"unread":{{all.Count}}}""", await counted.Content.ReadAsStringAsync(Ct));

        var first = all[0].GetProperty("id").GetGuid();
        var marked = await _admin.PostAsync($"/api/v1/notifications/{first}/read", null, Ct);
        Assert.Equal((HttpStatusCode.NoContent, "notification.mark"), (marked.StatusCode, Action(marked)));
        var missing = await _admin.PostAsync($"/api/v1/notifications/{Guid.NewGuid()}/read", null, Ct);
        Assert.Equal((HttpStatusCode.NotFound, "notification.mark"), (missing.StatusCode, Action(missing)));
        // Someone else's notification is not found either.
        var other = await UserAsync("tech1");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/notifications/{all[1].GetProperty("id").GetGuid()}/read", null, Ct)).StatusCode);
        Assert.Equal("[]", await other.GetStringAsync("/api/v1/notifications", Ct));

        var unread = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/notifications?unread=true", Ct);
        Assert.Equal(all.Count - 1, unread!.Count);
        Assert.DoesNotContain(unread, n => n.GetProperty("id").GetGuid() == first);
        Assert.Equal(all.Count, (await _admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/notifications?unread=false", Ct))!.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/api/v1/notifications?unread=maybe", Ct)).StatusCode);

        var allMarked = await _admin.PostAsync("/api/v1/notifications/read-all", null, Ct);
        Assert.Equal((HttpStatusCode.NoContent, "notification.mark.all"), (allMarked.StatusCode, Action(allMarked)));
        Assert.Equal("""{"unread":0}""", await _admin.GetStringAsync("/api/v1/notifications/count", Ct));
    }

    [Fact]
    public async Task Decide_RefusedByHook_Is422_RequestStaysPending()
    {
        var request = await IdAsync(await RequestAsync(_admin, _space));
        await SetAsync("Refuse", "access.decide");

        var r = await DecideAsync(_admin, request);

        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", (await JsonAsync(r)).GetProperty("detail").GetString());
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(AccessRequestStatus.Pending, (await db.AccessRequests.SingleAsync(a => a.Id == request, Ct)).Status);
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.AccessDecide, Ct));
        });
    }

    /// <summary>The core says no before any plugin code has seen the action.</summary>
    [Fact]
    public async Task APersonWhoMayNot_IsRefused_BeforeAnyHook()
    {
        var tech = await UserAsync("tech1");
        var hidden = await SpaceAsync("hr", "HR", sensitive: true);
        var request = await IdAsync(await RequestAsync(tech, _space));

        var decided = await DecideAsync(tech, request);
        var opened = await EmergencyAsync(tech, hidden, "let me in please");

        // No role in the request's space: the request does not exist for this caller (404, not 403).
        Assert.Equal((HttpStatusCode.NotFound, "access.decide"), (decided.StatusCode, Action(decided)));
        Assert.Equal((HttpStatusCode.Forbidden, "space.sensitive.emergency"), (opened.StatusCode, Action(opened)));
        var counters = await CountersAsync();
        Assert.True(counters.TryGetProperty("before:access.request", out _));
        Assert.False(counters.TryGetProperty("before:access.decide", out _));
        Assert.False(counters.TryGetProperty("before:space.sensitive.emergency", out _));
        // As before: a missing reason is answered before the caller is looked at.
        Assert.Equal(HttpStatusCode.BadRequest, (await EmergencyAsync(tech, hidden, "")).StatusCode);
        await _f.WithDbAsync(async db => Assert.Equal(AccessRequestStatus.Pending, (await db.AccessRequests.SingleAsync(a => a.Id == request, Ct)).Status));
    }
}
