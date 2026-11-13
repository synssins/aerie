using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web;
using Kb.Web.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>A plugin's scheduled jobs: run when due, by one server, as the plugin; a failure is recorded and the next slot runs.</summary>
public sealed class JobTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private const string Probe = "/api/v1/plugins/kb.test.probe";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    /// <summary>Installs the probe with schedules in its manifest and restarts so it is loaded.</summary>
    private async Task InstallAsync(params PluginSchedule[] schedules)
    {
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(TestPlugins.Probe, m => m with { Schedules = schedules }));
        await RestartAsync();
    }

    private async Task SetAsync(string key, string value) =>
        (await _admin.PutAsJsonAsync($"{Url}/kb.test.probe/settings", new { values = new Dictionary<string, string> { [key] = value } }, Ct)).EnsureSuccessStatusCode();

    private static Task<int> RunAsync(KbAppFactory f) => f.Services.GetRequiredService<JobRunner>().RunOnceAsync(Ct);
    private Task<int> RunAsync() => RunAsync(_f);

    private async Task<JobRun> RowAsync()
    {
        JobRun row = null!;
        await _f.WithDbAsync(async db => row = await db.JobRuns.AsNoTracking().SingleAsync(r => r.PluginId == "kb.test.probe" && r.Name == "tick", Ct));
        return row;
    }

    private Task MakeDueAsync() => _f.WithDbAsync(db => db.JobRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.NextRunAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

    private static async Task<int> RunsAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct)).GetProperty("byAction").TryGetProperty("job:tick", out var n) ? n.GetInt32() : 0;

    private async Task<(PluginState State, string? Reason)> StatusAsync()
    {
        (PluginState, string?) status = default;
        await _f.WithDbAsync(async db => status = await db.PluginStatuses.Where(s => s.PluginId == "kb.test.probe").Select(s => new ValueTuple<PluginState, string?>(s.State, s.Reason)).SingleAsync(Ct));
        return status;
    }

    [Fact]
    public async Task Job_RunsWhenDue_ThenWaitsForItsNextSlot()
    {
        await InstallAsync(new PluginSchedule("tick", 5));

        Assert.Equal(1, await RunAsync()); // a job runs the first time it is seen
        Assert.Equal(0, await RunAsync()); // and not again before its next slot
        Assert.Equal(1, await RunsAsync(_admin));
        var row = await RowAsync();
        Assert.NotNull(row.LastRunAt);
        Assert.InRange(row.NextRunAt - row.LastRunAt.Value, TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1));
        Assert.Null(row.ClaimedBy);

        await MakeDueAsync();
        Assert.Equal(1, await RunAsync());
        Assert.Equal(2, await RunsAsync(_admin));
    }

    [Fact]
    public async Task Job_TwoServers_RunsOnce()
    {
        await InstallAsync(new PluginSchedule("tick", 5));
        await SetAsync("JobDelay", "300");
        await using var second = _f.SecondInstance();
        var other = await second.SignedInBreakGlassAsync();

        var ran = await Task.WhenAll(RunAsync(_f), RunAsync(second));

        Assert.Equal(1, ran.Sum());
        Assert.Equal(1, await RunsAsync(_admin) + await RunsAsync(other));
    }

    [Fact]
    public async Task Job_Throws_IsRecorded_RunsAgainNextSlot()
    {
        await InstallAsync(new PluginSchedule("tick", 5));
        await SetAsync("FailOn", "job");

        Assert.Equal(1, await RunAsync());

        var failed = await RowAsync();
        Assert.Equal("Probe job failed.", failed.LastError);
        Assert.True(failed.NextRunAt > DateTimeOffset.UtcNow.AddMinutes(4)); // it waits for its next slot, it does not spin

        await SetAsync("FailOn", "");
        await MakeDueAsync();
        Assert.Equal(1, await RunAsync());
        Assert.Null((await RowAsync()).LastError);
        Assert.Equal(1, await RunsAsync(_admin));
    }

    [Fact]
    public async Task Job_ThatKeepsFailing_GetsItsPluginDisabled()
    {
        await InstallAsync(new PluginSchedule("tick", 5));
        await SetAsync("FailOn", "job");

        for (var i = 0; i < Kb.Web.Plugins.PluginFailures.Limit; i++)
        {
            await MakeDueAsync();
            Assert.Equal(1, await RunAsync());
        }
        await _f.Services.GetRequiredService<Kb.Web.Plugins.PluginFailures>().Pending;

        Assert.Equal((PluginState.DisabledByCore, "job tick: Probe job failed."), await StatusAsync());
    }

    [Fact]
    public async Task Job_ActsAsItsPlugin()
    {
        await InstallAsync(new PluginSchedule("tick", 5));
        var space = (await (await _admin.PostAsJsonAsync("/api/v1/spaces", new { key = "sd", name = "Service Desk" }, Ct)).Content.ReadFromJsonAsync<SpaceDto>(Ct))!.Id;
        var page = (await (await _admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Page", title = "Reset password" }, Ct)).Content.ReadFromJsonAsync<PageDto>(Ct))!.Id;
        await SetAsync("JobPage", page.ToString());

        await RunAsync();

        var counters = await _admin.GetFromJsonAsync<JsonElement>($"{Probe}/counters", Ct);
        Assert.Equal("Plugin:kb.test.probe:job", counters.GetProperty("lastJob").GetString());
        Assert.Equal("Reset password", counters.GetProperty("lastJobRead").GetString()); // a plugin reads a page of a space that is not sensitive
    }

    [Fact]
    public async Task Health_ACheckThatThrows_IsUnhealthy_AndAFailedJobShowsItsError()
    {
        await InstallAsync(new PluginSchedule("tick", 5));
        await SetAsync("Register", "health");
        await RestartAsync();
        await SetAsync("FailOn", "job");
        await RunAsync();

        var probe = (await _admin.GetFromJsonAsync<JsonElement>($"{Url}/health", Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.test.probe");

        var check = Assert.Single(probe.GetProperty("checks").EnumerateArray());
        Assert.Equal(("remote", "Unhealthy", "Probe health failed."), (check.GetProperty("name").GetString(), check.GetProperty("status").GetString(), check.GetProperty("text").GetString()));
        Assert.Equal("Probe job failed.", Assert.Single(probe.GetProperty("jobs").EnumerateArray()).GetProperty("lastError").GetString());
        // Looking at the health of a plugin is not a failure of the plugin.
        Assert.Equal(PluginState.Enabled, (await StatusAsync()).State);
    }

    [Fact]
    public async Task Schedule_WithoutAJob_DisablesThePluginAtLoad()
    {
        await InstallAsync(new PluginSchedule("nightly", 60));
        Assert.Equal((PluginState.DisabledByCore, "No job named nightly."), await StatusAsync());
    }

    [Fact]
    public async Task Job_WithoutASchedule_DisablesThePluginAtLoad()
    {
        await InstallAsync();
        await SetAsync("Register", "job");
        await RestartAsync();
        Assert.Equal((PluginState.DisabledByCore, "No schedule named tick."), await StatusAsync());
    }

    [Fact]
    public async Task Schedule_UnderOneMinute_DisablesThePluginAtLoad()
    {
        await InstallAsync(new PluginSchedule("tick", 0));
        Assert.Equal((PluginState.DisabledByCore, "Schedule tick: everyMinutes must be 1 or more."), await StatusAsync());
    }
}
