using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Kb.Tests.Integration.Plugins;

public sealed class FailureTests : IAsyncLifetime
{
    private const string Url = "/api/v1/admin/plugins";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await _f.SignedInBreakGlassAsync();
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task InstallAsync(string project, Func<PluginManifest, PluginManifest>? change = null, params (string Key, string Value)[] settings)
    {
        await TestPlugins.InstallAsync(_admin, TestPlugins.Package(project, change));
        if (settings.Length == 0) return;
        var id = PluginPackageReader.Read(TestPlugins.Package(project, change)).Value!.Manifest.Id;
        await _f.WithDbAsync(async db =>
        {
            foreach (var (key, value) in settings) db.PluginSettingValues.Add(new PluginSettingValue { PluginId = id, Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
    }

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    /// <summary>A failure counter on this app with a clock the test moves.</summary>
    private PluginFailures Failures() => new(_f.Services.GetRequiredService<IServiceScopeFactory>(), _f.Services.GetRequiredService<PluginRegistry>(), _clock, NullLogger<PluginFailures>.Instance);

    private async Task<PluginStatus> StatusAsync(string id)
    {
        PluginStatus? status = null;
        await _f.WithDbAsync(async db => status = await db.PluginStatuses.AsNoTracking().SingleAsync(s => s.PluginId == id, Ct));
        return status!;
    }

    [Fact]
    public async Task FiveFailuresInTenMinutes_DisablesThePlugin_WithReason()
    {
        await InstallAsync(TestPlugins.Probe);
        await RestartAsync();
        var failures = Failures();
        for (var i = 0; i < 4; i++)
        {
            failures.Report("kb.test.probe", "page.save hook", new InvalidOperationException("The remote system said no.\nStack follows."));
            _clock.Advance(TimeSpan.FromMinutes(2));
        }
        await failures.Pending;
        Assert.Equal(PluginState.Enabled, (await StatusAsync("kb.test.probe")).State);

        failures.Report("kb.test.probe", "page.save hook", new InvalidOperationException("The remote system said no.\nStack follows."));
        await failures.Pending;

        var status = await StatusAsync("kb.test.probe");
        Assert.Equal((PluginState.DisabledByCore, "page.save hook: The remote system said no."), (status.State, status.Reason));
        Assert.Null(status.LockedAt); // not an enforcing plugin
        Assert.Single(_f.Restarter.Reasons);
        await _f.WithDbAsync(async db => Assert.Contains("The remote system said no.", (await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.PluginDisabledByCore, Ct)).DataJson));
    }

    [Fact]
    public async Task FourFailures_ThenTenQuietMinutes_ThenOne_DoesNotDisable()
    {
        await InstallAsync(TestPlugins.Probe);
        await RestartAsync();
        var failures = Failures();
        for (var i = 0; i < 4; i++) failures.Report("kb.test.probe", "job");
        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        failures.Report("kb.test.probe", "job");
        await failures.Pending;
        Assert.Equal(PluginState.Enabled, (await StatusAsync("kb.test.probe")).State);
    }

    [Fact]
    public async Task Report_ForSomethingThatIsNotALoadedPlugin_IsIgnored()
    {
        var failures = Failures();
        for (var i = 0; i < 10; i++)
        {
            failures.Report("core", "audit sink", new InvalidOperationException("x"));
            failures.Report("kb.nothing", "audit sink");
        }
        await failures.Pending;
        Assert.Empty(_f.Restarter.Reasons);
    }

    [Fact]
    public async Task EnforcingPlugin_DisabledByCore_IsLocked_AndReleaseLock_ClearsIt_AndIsAudited()
    {
        await InstallAsync(TestPlugins.Probe, m => m with { Enforcing = true });
        await RestartAsync();
        var failures = Failures();
        for (var i = 0; i < 5; i++) failures.Report("kb.test.probe", "page.save hook");
        await failures.Pending;

        var status = await StatusAsync("kb.test.probe");
        Assert.Equal((PluginState.DisabledByCore, "page.save hook"), (status.State, status.Reason));
        Assert.NotNull(status.LockedAt);
        var listed = (await _admin.GetFromJsonAsync<JsonElement>(Url, Ct)).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "kb.test.probe");
        Assert.True(listed.GetProperty("locked").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"{Url}/kb.test.probe/release-lock", null, Ct)).StatusCode);
        status = await StatusAsync("kb.test.probe");
        Assert.Null(status.LockedAt);
        Assert.Equal(PluginState.DisabledByCore, status.State); // released, still off
        await _f.WithDbAsync(async db => Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.PluginLockRelease && a.TargetId == "kb.test.probe", Ct)));
    }

    [Fact]
    public async Task DisabledByAdmin_IsNotLocked()
    {
        await InstallAsync(TestPlugins.Probe, m => m with { Enforcing = true });
        await RestartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsJsonAsync($"{Url}/kb.test.probe/state", new { enabled = false }, Ct)).StatusCode);
        var status = await StatusAsync("kb.test.probe");
        Assert.Equal((PluginState.Disabled, (DateTimeOffset?)null), (status.State, status.LockedAt));
    }

    [Fact]
    public async Task DisabledByCore_AlsoDisablesDependents()
    {
        await InstallAsync(TestPlugins.Lib);
        await InstallAsync(TestPlugins.Dependent);
        await RestartAsync();
        var failures = Failures();
        for (var i = 0; i < 5; i++) failures.Report("kb.test.lib", "job", new InvalidOperationException("Broken."));
        await failures.Pending;

        Assert.Equal((PluginState.DisabledByCore, "job: Broken."), ((await StatusAsync("kb.test.lib")).State, (await StatusAsync("kb.test.lib")).Reason));
        Assert.Equal((PluginState.DisabledByCore, "Requires Test library."), ((await StatusAsync("kb.test.dependent")).State, (await StatusAsync("kb.test.dependent")).Reason));
        await _f.WithDbAsync(async db => Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.PluginDisabledByCore, Ct)));
    }

    [Fact]
    public async Task FailingAuditSink_IsReported_AuditEntryStillWritten()
    {
        await InstallAsync(TestPlugins.Probe, settings: ("FailOn", "sink"));
        await RestartAsync(); // the sign-in here already writes entries its sink fails on

        for (var i = 0; i < 5; i++) await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = $"Space {i}" }, Ct);
        var status = await StatusAsync("kb.test.probe");
        for (var waited = 0; status.State == PluginState.Enabled && waited < 5000; waited += 100)
        {
            await Task.Delay(100, Ct);
            status = await StatusAsync("kb.test.probe");
        }

        Assert.Equal((PluginState.DisabledByCore, "audit sink: Probe sink failed."), (status.State, status.Reason));
        await _f.WithDbAsync(async db => Assert.Equal(5, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.SpaceCreate, Ct))); // every entry was still written
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/health/ready", Ct)).StatusCode);
    }

    // Finding 4: a sink that times out is a failing sink, not a cancelled request.
    [Fact]
    public async Task AuditSinkThatTimesOut_IsReported_AuditEntryStillWritten()
    {
        await InstallAsync(TestPlugins.Probe, settings: [("FailOn", "sink"), ("FailWith", "timeout")]);
        await RestartAsync();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync("/api/v1/spaces", new { name = $"Space {i}" }, Ct)).StatusCode);
        var status = await StatusAsync("kb.test.probe");
        for (var waited = 0; status.State == PluginState.Enabled && waited < 5000; waited += 100)
        {
            await Task.Delay(100, Ct);
            status = await StatusAsync("kb.test.probe");
        }
        Assert.Equal((PluginState.DisabledByCore, "audit sink: Probe sink timed out."), (status.State, status.Reason));
    }
}
