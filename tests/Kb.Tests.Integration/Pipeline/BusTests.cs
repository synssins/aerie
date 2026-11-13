using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Kb.Web.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>The nine steps of the action bus, driven through the test probe's own actions and routes.</summary>
public sealed class BusTests : IAsyncLifetime
{
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        await RestartAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    /// <summary>Sets one probe setting. It applies at once, without a restart.</summary>
    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private Task<HttpResponseMessage> EchoAsync(string target, string text, HttpClient? c = null) => (c ?? _admin).PostAsJsonAsync($"{Probe}/echo", new { target, text }, Ct);
    private Task<JsonElement> CountersAsync() => _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct);

    private static async Task<string?> DetailAsync(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString();

    private async Task<List<string>> AuditAsync()
    {
        var actions = new List<string>();
        await _f.WithDbAsync(async db => actions = await db.AuditEntries.Where(a => a.Action.StartsWith("probe.")).OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync(Ct));
        return actions;
    }

    [Fact]
    public async Task Send_RunsTheHandler_AsTheCaller_AndWritesItsAudit()
    {
        var r = await EchoAsync("a", "hello");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("hello", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("text").GetString());

        var counters = await CountersAsync();
        Assert.Equal((1, 1), (counters.GetProperty("hooks").GetInt32(), counters.GetProperty("handled").GetInt32()));
        Assert.Equal("hello", counters.GetProperty("written").GetProperty("a").GetString());
        var me = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/me", Ct);
        Assert.Equal($"User:{me.GetProperty("id").GetString()}", counters.GetProperty("lastActor").GetString());
        Assert.Equal(["probe.attempt", "probe.echo"], await AuditAsync());
    }

    [Fact]
    public async Task Send_AuthorizesBeforeAnyHook()
    {
        await SetAsync("Deny", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await EchoAsync("a", "hello")).StatusCode);
        var counters = await CountersAsync();
        Assert.Equal((0, 0), (counters.GetProperty("hooks").GetInt32(), counters.GetProperty("handled").GetInt32()));
    }

    [Fact]
    public async Task Hook_Refusal_Is422_WithItsMessage()
    {
        await SetAsync("Refuse", "probe.echo");
        var r = await EchoAsync("a", "hello");
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Probe refused.", await DetailAsync(r));
        Assert.Empty((await CountersAsync()).GetProperty("written").EnumerateObject());
    }

    [Fact]
    public async Task Hook_Throws_RefusesAndReports()
    {
        await SetAsync("FailOn", "before");
        for (var i = 0; i < PluginFailures.Limit; i++)
        {
            var r = await EchoAsync("a", "hello");
            Assert.Equal((HttpStatusCode)422, r.StatusCode);
            Assert.Equal("Test probe failed.", await DetailAsync(r));
        }
        await _f.Services.GetRequiredService<PluginFailures>().Pending;
        await _f.WithDbAsync(async db =>
        {
            var status = await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct);
            Assert.Equal((PluginState.DisabledByCore, "probe.echo hook: Probe hook failed."), (status.State, status.Reason)); // five reports turned it off
        });
    }

    [Fact]
    public async Task OptionalHook_Throws_ActionGoesOn_Reported()
    {
        await _f.WithDbAsync(async db =>
        {
            db.PluginSettingValues.Add(new PluginSettingValue { PluginId = "kb.test.probe", Key = "Optional", Value = "true", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
        await RestartAsync(); // how a hook is registered is decided when the plugin is configured
        await SetAsync("FailOn", "before");

        for (var i = 0; i < PluginFailures.Limit; i++) Assert.Equal(HttpStatusCode.OK, (await EchoAsync("a", "hello")).StatusCode);
        await _f.Services.GetRequiredService<PluginFailures>().Pending;
        await _f.WithDbAsync(async db => Assert.Equal(PluginState.DisabledByCore, (await db.PluginStatuses.SingleAsync(s => s.PluginId == "kb.test.probe", Ct)).State)); // skipped, and still reported
    }

    [Fact]
    public async Task Hook_CanChangeThePayload()
    {
        await SetAsync("Rewrite", "probe.echo:Text=rewritten");
        var r = await EchoAsync("a", "hello");
        Assert.Equal("rewritten", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("text").GetString());
        Assert.Equal("rewritten", (await CountersAsync()).GetProperty("written").GetProperty("a").GetString());
    }

    [Fact]
    public async Task Hook_ReplacingAction_IsAuthorizedAgain()
    {
        await SetAsync("SwapTarget", "true"); // the hook points the action at a target its caller may not write
        Assert.Equal(HttpStatusCode.Forbidden, (await EchoAsync("a", "hello")).StatusCode);
        var counters = await CountersAsync();
        Assert.Empty(counters.GetProperty("written").EnumerateObject());
        Assert.Equal(0, counters.GetProperty("handled").GetInt32());
    }

    [Fact]
    public async Task Filter_ShapesTheResult_OfARead()
    {
        await EchoAsync("a", "hello");
        var peek = await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/peek/a", Ct);
        Assert.Equal("HELLO", peek.GetProperty("text").GetString());             // the filter shaped what was read
        Assert.Equal("hello", (await CountersAsync()).GetProperty("written").GetProperty("a").GetString()); // not what is stored
    }

    [Fact]
    public async Task Filter_Throws_RefusesTheAction()
    {
        await EchoAsync("a", "hello");
        await SetAsync("FailOn", "filter");
        var r = await _admin.GetAsync($"{Probe}/peek/a", Ct);
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Test probe failed.", await DetailAsync(r));
    }

    [Fact]
    public async Task Audit_EvenIfRefused_IsWrittenWhenTheActionFails_AnOrdinaryEntryIsNot()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await EchoAsync("a", "fail")).StatusCode);
        Assert.Equal(["probe.attempt"], await AuditAsync());
        Assert.Equal(HttpStatusCode.OK, (await EchoAsync("a", "hello")).StatusCode);
        Assert.Equal(["probe.attempt", "probe.attempt", "probe.echo"], await AuditAsync());
    }

    [Fact]
    public async Task Nested_Send_RunsOnItsOwn()
    {
        await SetAsync("Nested", "true"); // the handler sends a second echo, then fails
        Assert.Equal(HttpStatusCode.Conflict, (await EchoAsync("a", "fail")).StatusCode);
        var written = (await CountersAsync()).GetProperty("written");
        Assert.Equal("inner", written.GetProperty("nested").GetString()); // the inner action stands
        Assert.False(written.TryGetProperty("a", out _));
    }

    [Fact]
    public async Task PluginRoute_NeedsSignIn_UnlessItSaysAnonymous_AndIsClosedToTokens()
    {
        var anonymous = _f.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Probe}/counters", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EchoAsync("a", "hello", anonymous)).StatusCode);
        Assert.Equal("anonymous", (await anonymous.GetFromJsonAsync<JsonElement>($"{Probe}/hello", Ct)).GetProperty("hello").GetString());

        var made = await (await _admin.PostAsJsonAsync("/api/v1/tokens", new { name = "t", kind = "Personal", days = 1, scopes = new[] { new { spaceId = (Guid?)null, access = "Write" } } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = _f.NewClient();
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync($"{Probe}/counters", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EchoAsync("a", "hello", token)).StatusCode);
    }

    [Fact]
    public async Task StaleInstance_Answers503_OnPluginRoutes()
    {
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"{Probe}/counters", Ct)).StatusCode);
        // Another server changed the plugins: this one runs an old set until it has restarted.
        await _f.WithDbAsync(async db =>
        {
            (await db.RuntimeSettings.SingleAsync(s => s.Key == "Plugins.Generation", Ct)).Value = "99";
            await db.SaveChangesAsync(Ct);
        });
        await _f.Services.GetRequiredService<PluginWatcher>().CheckAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _admin.GetAsync($"{Probe}/counters", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/v1/me", Ct)).StatusCode); // the core's own routes go on
    }

    // Phase 1 review, finding 4, in the bus: a hook whose HTTP call times out refuses the action; it does not look like the request being cancelled.
    [Fact]
    public async Task Hook_TimesOut_RefusesTheAction()
    {
        await SetAsync("FailWith", "timeout");
        await SetAsync("FailOn", "before");
        var r = await EchoAsync("a", "hello");
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("Test probe failed.", await DetailAsync(r));
    }

    /// <summary>
    /// A handler asks for its audit entry once its work is saved. If it then throws (building the answer fails), the work is done
    /// and the entry is still written.
    /// </summary>
    [Fact]
    public async Task Handler_ThrowsAfterItsAuditEntry_TheEntryIsWritten()
    {
        var r = await EchoAsync("a", "throw");

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Contains("probe.echo", await AuditAsync());
    }

    /// <summary>A plugin's routes are closed to API tokens in this round: a token reaches only the core routes listed as open.</summary>
    [Fact]
    public async Task PluginRoute_Token_403()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/tokens", new { name = "Agent", kind = "Personal", scopes = new[] { new { spaceId = (Guid?)null, access = "Write" } }, days = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = _f.NewClient();
        token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync($"{Probe}/counters", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync($"{Probe}/echo", new { target = "t", text = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/v1/me", Ct)).StatusCode); // the token itself is good
    }
}
