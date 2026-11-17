using Kb.Core.Plugins;
using Kb.Plugin.Sample;
using Kb.Plugins;

namespace Kb.Tests.Unit;

/// <summary>Sign-in methods (contract 0.8.0): how a plugin registers one, and the settings the app keeps for it, which the plugin cannot read.</summary>
public class SignInMethodRulesTests
{
    private sealed class Collect : IPluginApp
    {
        public readonly List<object> All = [];
        public IPluginApp Add<T>(T registration) where T : class { All.Add(registration); return this; }
    }

    [Fact]
    public void AMethod_HasAnIdForItsAddress_AndAButtonText_AnythingElseIsRefused()
    {
        var app = new Collect();
        app.SignInMethod<SampleSignIn>("example-2", " Sign in with Example ID ");
        Assert.Equal(new SignInMethodReg("example-2", "Sign in with Example ID", typeof(SampleSignIn)), Assert.Single(app.All));
        foreach (var bad in new[] { "", "-x", "Upper", "a.b", "a/b", "a b", new string('a', 33) })
            Assert.Throws<ArgumentException>(() => app.SignInMethod<SampleSignIn>(bad, "Name"));
        foreach (var bad in new[] { "", " ", new string('n', 61), "a\nb" })
            Assert.Throws<ArgumentException>(() => app.SignInMethod<SampleSignIn>("x", bad));
    }

    [Fact]
    public void TheAppsSettings_ForAMethod_AreItsClientIdSecretAndNewAccounts_OffByDefault()
    {
        var one = SignInSettings.For([("example", "Sign in with Example ID")]).ToList();
        Assert.Equal(["signIn.example.clientId", "signIn.example.clientSecret", "signIn.example.newAccounts"], one.Select(s => s.Key));
        Assert.Equal(["Client ID", "Client secret", "New accounts"], one.Select(s => s.Label));
        Assert.True(one[1].Secret);
        Assert.Equal(("toggle", "false"), (one[2].Type, one[2].Default));
        // With two methods, each label says which.
        Assert.Equal("Client ID (B)", SignInSettings.For([("a", "A"), ("b", "B")]).ElementAt(3).Label);
    }

    [Fact]
    public void AManifest_CannotDeclareTheAppsKeys()
    {
        var m = new PluginManifest
        {
            Id = "kb.x", Version = "1.0.0", Publisher = "test", MinKbVersion = "0.1.0", AbstractionsVersion = "0.8.0", Settings = [new PluginSetting("SignIn.x.clientId", "Mine")],
        };
        Assert.Equal("Setting SignIn.x.clientId: keys starting with \"signIn.\" are the app's.", PluginVersions.Refusal(m));
    }

    [Fact]
    public async Task ThePlugin_ReadsNoneOfTheAppsSettings_EvenWhenSaved()
    {
        var m = new PluginManifest { Id = "kb.x", Version = "1.0.0", Publisher = "test", MinKbVersion = "0.1.0", AbstractionsVersion = "0.8.0" };
        var snapshot = new PluginSettingsSnapshot { SecretReader = (_, _, _) => Task.FromResult<string?>("the-secret") };
        snapshot.Replace([("kb.x", "signIn.example.clientId", "client-1"), ("kb.x", "Own", "mine")]);
        var context = new PluginContext(m, snapshot);
        Assert.Equal((null, "mine"), (context.GetSetting("signIn.example.clientId"), context.GetSetting("Own")));
        Assert.Null(await context.GetSecretAsync("signIn.example.clientSecret", TestContext.Current.CancellationToken));
        Assert.Equal("the-secret", await context.GetSecretAsync("OwnSecret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThisHost_Is0_8_0_AndStillLoadsPluginsBuiltFor0_7_0()
    {
        Assert.Equal(("0.8.0", "0.8.0"), (KbHost.Version, KbHost.AbstractionsVersion));
        var old = new PluginManifest { Id = "kb.x", Version = "1.0.0", Publisher = "test", MinKbVersion = "0.7.0", AbstractionsVersion = "0.7.0" };
        Assert.Null(PluginVersions.Refusal(old));
    }
}
