using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kb.Tests.Integration.Pipeline;

/// <summary>
/// A plugin can define its own extension point and its own action, and another plugin can use them, with no code in the core for either:
/// the same registrations and the same bus carry them. (That a required plugin cannot be disabled while its dependent is enabled is
/// LifecycleTests.DisableOrRemove_WithEnabledDependent_409_AndEnable_WithDisabledRequirement_409.)
/// </summary>
public sealed class LibraryPluginTests : IAsyncLifetime
{
    private const string Lib = "/api/v1/plugins/kb.test.lib";
    private KbAppFactory _f = new(KbAppFactory.NewDbPath());
    private HttpClient _admin = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Lib));
        await TestPlugins.InstallAsync(admin, TestPlugins.Package(TestPlugins.Dependent));
        _f = await _f.RestartAsync();
        _admin = await _f.SignedInBreakGlassAsync();
    }

    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task LibraryPlugin_SeesWhatItsDependentRegistered()
    {
        var things = await _admin.GetFromJsonAsync<JsonElement>($"{Lib}/things", Ct);

        var thing = Assert.Single(things.EnumerateArray());
        Assert.Equal(("kb.test.dependent", "from the dependent"), (thing.GetProperty("plugin").GetString(), thing.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task LibraryPlugin_DefinesAnAction_ItsDependentHooksIt()
    {
        var r = await _admin.GetAsync($"{Lib}/greet/Cara", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("lib.greet", r.Headers.GetValues("X-Kb-Action").Single());
        // The library's handler ran the dependent's greeter, on the name the dependent's hook had changed.
        Assert.Equal("Hello, CARA.", (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("text").GetString());
    }
}
