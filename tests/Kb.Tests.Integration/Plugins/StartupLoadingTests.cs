using System.IO.Compression;
using System.Net;
using Kb.Core.Audit;
using Kb.Core.Plugins;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Plugins;

/// <summary>Packages stored in the database are unpacked and loaded before the web host is built. Rows are written directly here; installing is task 1.4.</summary>
public sealed class StartupLoadingTests : IAsyncLifetime
{
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _f.NewClient().GetAsync("/health/live", Ct); // creates the database
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    /// <summary>Stores a package the way an install will, then restarts.</summary>
    private async Task SeedAsync(string project, Func<PluginManifest, PluginManifest>? change = null, PluginState state = PluginState.Enabled, params (string Key, string Value)[] settings)
    {
        var bytes = TestPlugins.Package(project, change);
        var content = PluginPackageReader.Read(bytes).Value!;
        await _f.WithDbAsync(async db =>
        {
            db.PluginPackages.Add(new PluginPackage
            {
                PluginId = content.Manifest.Id, Version = content.Manifest.Version, Bytes = bytes, Sha256 = content.Sha256,
                ManifestJson = PluginManifests.Write(content.Manifest), Source = "upload", InstalledAt = DateTimeOffset.UtcNow,
            });
            db.PluginStatuses.Add(new PluginStatus { PluginId = content.Manifest.Id, State = state, ActiveVersion = content.Manifest.Version, UpdatedAt = DateTimeOffset.UtcNow });
            foreach (var (key, value) in settings)
                db.PluginSettingValues.Add(new PluginSettingValue { PluginId = content.Manifest.Id, Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });
    }

    private async Task RestartAsync()
    {
        _f = await _f.RestartAsync();
        _ = _f.Services; // the test host starts the app at first use
    }

    private bool SinkOf(string id) => _f.Services.GetServices<IAuditSink>().Any(s => s.ProviderId == id);
    private PluginInfo Info(string id) => _f.Services.GetRequiredService<PluginRegistry>().Find(id)!;

    private async Task<PluginStatus> StatusAsync(string id)
    {
        PluginStatus? status = null;
        await _f.WithDbAsync(async db => status = await db.PluginStatuses.AsNoTracking().SingleAsync(s => s.PluginId == id, Ct));
        return status!;
    }

    [Fact]
    public async Task EnabledPackage_LoadsAtStartup()
    {
        Assert.False(SinkOf("kb.test.probe"));
        await SeedAsync(TestPlugins.Probe);
        await RestartAsync();

        Assert.True(SinkOf("kb.test.probe"));
        var info = Info("kb.test.probe");
        Assert.Equal((true, PluginOrigin.Uploaded, PluginState.Enabled, "upload"), (info.Loaded, info.Origin, info.State, info.Source));
        Assert.Equal("Test probe", info.Manifest.Name);
    }

    [Fact]
    public async Task DisabledPackage_IsListed_NotLoaded()
    {
        await SeedAsync(TestPlugins.Probe, state: PluginState.Disabled);
        await RestartAsync();
        Assert.False(SinkOf("kb.test.probe"));
        Assert.Equal((false, PluginState.Disabled), (Info("kb.test.probe").Loaded, Info("kb.test.probe").State));
    }

    [Fact]
    public async Task ThrowingPlugin_IsDisabledByCore_AppStarts()
    {
        await SeedAsync(TestPlugins.Probe, settings: ("FailOn", "configure"));
        await RestartAsync();

        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync("/health/ready", Ct)).StatusCode);
        var status = await StatusAsync("kb.test.probe");
        Assert.Equal((PluginState.DisabledByCore, "Probe failed in Configure."), (status.State, status.Reason));
        Assert.False(Info("kb.test.probe").Loaded);
        Assert.False(SinkOf("kb.test.probe")); // what it registered before it threw is not kept
        await _f.WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.PluginDisabledByCore, Ct);
            Assert.Equal("kb.test.probe", entry.TargetId);
            Assert.Contains("Probe failed in Configure.", entry.DataJson);
        });

        // It stays disabled: the next start does not try it again.
        await RestartAsync();
        Assert.Equal(PluginState.DisabledByCore, (await StatusAsync("kb.test.probe")).State);
        await _f.WithDbAsync(async db => Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.PluginDisabledByCore, Ct)));
    }

    [Fact]
    public async Task ThrowingPlugin_LeavesNoServices()
    {
        await SeedAsync(TestPlugins.Probe, settings: ("FailOn", "services"));
        await RestartAsync();
        Assert.False(SinkOf("kb.test.probe"));
        Assert.Equal("Probe failed in ConfigureServices.", (await StatusAsync("kb.test.probe")).Reason);
    }

    [Fact]
    public async Task Dependent_OfDisabledPlugin_IsDisabledWithReason()
    {
        await SeedAsync(TestPlugins.Lib, m => m with { MinKbVersion = "9.0.0" });
        await SeedAsync(TestPlugins.Dependent);
        await RestartAsync();

        Assert.Equal((PluginState.DisabledByCore, "Needs version 9.0.0 or later."), ((await StatusAsync("kb.test.lib")).State, (await StatusAsync("kb.test.lib")).Reason));
        Assert.Equal((PluginState.DisabledByCore, "Requires Test library."), ((await StatusAsync("kb.test.dependent")).State, (await StatusAsync("kb.test.dependent")).Reason));
    }

    [Fact]
    public async Task SafeMode_LoadsNoUploadedPlugin_ChangesNoState()
    {
        await SeedAsync(TestPlugins.Probe, settings: ("FailOn", "configure"));
        await _f.DisposeAsync();
        _f = new KbAppFactory(_f.DbPath, settings: new Dictionary<string, string> { ["Kb:Plugins:SafeMode"] = "true" }, workFolder: _f.WorkFolder);

        var registry = _f.Services.GetRequiredService<PluginRegistry>();
        Assert.True(registry.SafeMode);
        Assert.Equal((false, PluginState.Enabled), (Info("kb.test.probe").Loaded, Info("kb.test.probe").State)); // listed, not loaded, not disabled
        Assert.Equal(PluginState.Enabled, (await StatusAsync("kb.test.probe")).State);
        Assert.True(Info("kb.email.smtp").Loaded); // built-in plugins still load
    }

    [Fact]
    public async Task HostAssemblies_AreShared()
    {
        var package = TestPlugins.Package(TestPlugins.Probe);
        using (var zip = new ZipArchive(new MemoryStream(package)))
            Assert.Contains(zip.Entries, e => e.FullName == "Kb.Plugins.Abstractions.dll"); // the package carries its own copy
        await SeedAsync(TestPlugins.Probe);
        await RestartAsync();

        // Its sink is an IAuditSink of the host, or the container could not hand it out as one.
        Assert.True(SinkOf("kb.test.probe"));
        Assert.Same(typeof(IKbPlugin).Assembly, _f.Services.GetServices<IAuditSink>().Single(s => s.ProviderId == "kb.test.probe").GetType().GetInterface(nameof(IAuditSink))!.Assembly);
    }

    [Fact]
    public async Task ExportedAssembly_IsSharedWithDependent()
    {
        await SeedAsync(TestPlugins.Dependent); // the order of the rows does not decide the load order
        await SeedAsync(TestPlugins.Lib);
        await RestartAsync();

        var registry = _f.Services.GetRequiredService<PluginRegistry>();
        Assert.True(Info("kb.test.lib").Loaded && Info("kb.test.dependent").Loaded);
        var contract = registry.AssemblyOf("kb.test.lib")!.GetType("Kb.TestPlugin.Lib.ILibGreeter", throwOnError: true)!;
        var greeter = Assert.Single(_f.Services.GetServices(contract)); // empty if Dependent had loaded its own copy of the library
        Assert.Equal("DependentGreeter", greeter!.GetType().Name);
        Assert.Equal("Hello, Pat.", contract.GetMethod("Greet")!.Invoke(greeter, ["Pat"]));
    }

    [Fact]
    public async Task BuiltIn_GetsStatusRow_Enabled()
    {
        var status = await StatusAsync("kb.email.smtp");
        Assert.Equal((PluginState.Enabled, (string?)null), (status.State, status.ActiveVersion));
        var info = Info("kb.email.smtp");
        Assert.Equal((true, PluginOrigin.BuiltIn, "built-in"), (info.Loaded, info.Origin, info.Source));
    }

    [Fact]
    public async Task GuardedClient_RefusesAnUndeclaredHost()
    {
        await SeedAsync(TestPlugins.Probe, m => m with { OutboundHosts = ["declared.example"] });
        await RestartAsync();
        var client = _f.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PluginHttp.ClientName("kb.test.probe"));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://undeclared.example/", Ct));
        Assert.Contains("undeclared.example", e.Message);
    }

    [Fact]
    public async Task UnpackFolder_ReusedWhenHashMatches_ReplacedWhenNot()
    {
        await SeedAsync(TestPlugins.Probe);
        await RestartAsync();
        var folder = Path.Combine(_f.WorkFolder, "kb.test.probe", "1.0.0");
        Assert.True(File.Exists(Path.Combine(folder, "Kb.TestPlugin.Probe.dll")));
        var stray = Path.Combine(folder, "stray.txt");
        await File.WriteAllTextAsync(stray, "left by a test", Ct);
        Directory.CreateDirectory(Path.Combine(_f.WorkFolder, "kb.test.probe", "0.9.0")); // an older version's folder

        await RestartAsync();
        Assert.True(File.Exists(stray)); // same package: the folder is used as it is
        Assert.False(Directory.Exists(Path.Combine(_f.WorkFolder, "kb.test.probe", "0.9.0")));

        await File.WriteAllTextAsync(Path.Combine(folder, ".sha256"), "another package", Ct);
        await RestartAsync();
        Assert.False(File.Exists(stray)); // not this package: unpacked again
        Assert.True(Info("kb.test.probe").Loaded);
    }

    [Fact]
    public async Task Plugin_RegisteringACoreService_DoesNotReplaceIt()
    {
        await SeedAsync(TestPlugins.Probe, settings: ("Register", "filestore"));
        await RestartAsync();
        Assert.True(Info("kb.test.probe").Loaded);
        Assert.Equal("ActiveFileStore", _f.Services.GetRequiredService<IFileStore>().GetType().Name);
    }

    [Fact]
    public async Task PluginWithHostedService_IsDisabledByCore_AppStarts()
    {
        await SeedAsync(TestPlugins.Probe, settings: ("Register", "hosted"));
        await RestartAsync();
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().GetAsync("/health/ready", Ct)).StatusCode);
        Assert.Equal((PluginState.DisabledByCore, "Hosted services are not supported."), ((await StatusAsync("kb.test.probe")).State, (await StatusAsync("kb.test.probe")).Reason));
    }

    [Fact]
    public async Task CorruptPackage_IsDisabledByCore_AppStarts()
    {
        await SeedAsync(TestPlugins.Probe);
        await _f.WithDbAsync(async db =>
        {
            (await db.PluginPackages.SingleAsync(Ct)).Bytes = [1, 2, 3];
            await db.SaveChangesAsync(Ct);
        });
        await RestartAsync();
        Assert.Equal((PluginState.DisabledByCore, "Not a plugin package."), ((await StatusAsync("kb.test.probe")).State, (await StatusAsync("kb.test.probe")).Reason));
    }
}
