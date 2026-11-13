using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Kb.Web.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>After handlers: they run from an in-memory queue once the action has succeeded, as the plugin that owns them.</summary>
public sealed class QueueTests : IAsyncLifetime
{
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestPlugins.InstallAsync(await _f.SignedInBreakGlassAsync(), TestPlugins.Package(TestPlugins.Probe));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync("/api/v1/admin/plugins/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private Task<HttpResponseMessage> EchoAsync(string target, string text) => _admin.PostAsJsonAsync($"{Probe}/echo", new { target, text }, Ct);
    private Task<JsonElement> CountersAsync() => _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct);

    /// <summary>Waits until a counter reaches a value; the queue runs beside the request.</summary>
    private async Task<JsonElement> UntilAsync(string counter, int value)
    {
        var counters = await CountersAsync();
        for (var waited = 0; counters.GetProperty(counter).GetInt32() < value && waited < 5000; waited += 50)
        {
            await Task.Delay(50, Ct);
            counters = await CountersAsync();
        }
        return counters;
    }

    [Fact]
    public async Task After_RunsOnceTheActionSucceeded_AsThePlugin()
    {
        Assert.Equal(HttpStatusCode.OK, (await EchoAsync("a", "hello")).StatusCode);
        var counters = await UntilAsync("after", 1);
        Assert.Equal(1, counters.GetProperty("after").GetInt32());
        // The handler got the action and its result, and ran as the plugin, not as the person who sent the action.
        Assert.Equal("probe.echo|a|hello|User|Plugin:kb.test.probe:queue", counters.GetProperty("lastAfter").GetString());
    }

    [Fact]
    public async Task After_DoesNotRun_WhenTheActionFailed_OrWasRefused()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await EchoAsync("a", "fail")).StatusCode);
        await SetAsync("Refuse", "probe.echo");
        Assert.Equal((HttpStatusCode)422, (await EchoAsync("a", "hello")).StatusCode);
        await SetAsync("Refuse", "");
        await EchoAsync("b", "marker"); // a later action whose handler does run: the queue has passed the two above
        var counters = await UntilAsync("after", 1);
        Assert.Equal(1, counters.GetProperty("after").GetInt32());
        Assert.StartsWith("probe.echo|b|marker|", counters.GetProperty("lastAfter").GetString());
    }

    [Fact]
    public async Task After_Throws_IsReported_ActionStands()
    {
        await SetAsync("FailOn", "after");
        for (var i = 0; i < PluginFailures.Limit; i++) Assert.Equal(HttpStatusCode.OK, (await EchoAsync($"t{i}", "hello")).StatusCode);

        Assert.Equal(PluginFailures.Limit, (await CountersAsync()).GetProperty("written").EnumerateObject().Count()); // every action stands
        var state = PluginState.Enabled;
        string? reason = null;
        for (var waited = 0; state == PluginState.Enabled && waited < 5000; waited += 50)
        {
            await Task.Delay(50, Ct);
            await _f.WithDbAsync(async db => (state, reason) = await db.PluginStatuses.Where(s => s.PluginId == "kb.test.probe").Select(s => new ValueTuple<PluginState, string?>(s.State, s.Reason)).SingleAsync(Ct));
        }
        Assert.Equal((PluginState.DisabledByCore, "probe.echo after handler: Probe after handler failed."), (state, reason));
    }

    [Fact]
    public async Task After_OnARead_IsQueuedAfterTheRead()
    {
        await EchoAsync("a", "hello");
        await _admin.GetAsync($"{Probe}/peek/a", Ct);
        Assert.Equal(1, (await UntilAsync("afterReads", 1)).GetProperty("afterReads").GetInt32());
    }

    [Fact]
    public async Task Stop_RunsWhatIsStillWaiting()
    {
        var file = Path.Combine(Path.GetTempPath(), $"kb-test-after-{Guid.NewGuid():N}.txt");
        await SetAsync("AfterFile", file);
        await SetAsync("AfterDelay", "400");
        for (var i = 0; i < 3; i++) await EchoAsync($"t{i}", $"text {i}");

        await _f.DisposeAsync(); // the app stops, as it does for every plugin change

        Assert.Equal(["text 0", "text 1", "text 2"], await File.ReadAllLinesAsync(file, Ct));
    }
}
