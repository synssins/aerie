using Kb.Core.Plugins;
using Kb.Plugin.Email.Smtp;
using Kb.Plugins;

namespace Kb.Tests.Unit;

public class PluginRulesTests
{
    private static PluginManifest M(string id, string version = "1.0.0", string? name = null, params PluginRequirement[] requires) =>
        new() { Id = id, Version = version, Publisher = "test", Name = name, Requires = requires };

    [Theory]
    [InlineData("kb.email.smtp", true)]
    [InlineData("ai-tools", true)]
    [InlineData("a1b", true)]
    [InlineData("Kb.X", false)]
    [InlineData("a..b", false)]
    [InlineData("-a", false)]
    [InlineData("a_b", false)]
    [InlineData("ab", false)]
    [InlineData(null, false)]
    public void Id_Valid_AcceptsDotsAndHyphens(string? id, bool valid) => Assert.Equal(valid, PluginId.IsValid(id));

    [Fact]
    public void Id_LongerThan64_IsNotValid()
    {
        Assert.True(PluginId.IsValid(new string('a', 64)));
        Assert.False(PluginId.IsValid(new string('a', 65)));
    }

    [Fact]
    public void TablePrefix_NeverPrefixOfAnother()
    {
        Assert.Equal("plg_ai__", PluginId.TablePrefix("ai"));
        Assert.Equal("plg_ai_tools__", PluginId.TablePrefix("ai-tools"));
        Assert.Equal("plg_kb_email_smtp__", PluginId.TablePrefix("kb.email.smtp"));
        Assert.False(PluginId.TablePrefix("ai-tools").StartsWith(PluginId.TablePrefix("ai"), StringComparison.Ordinal));
        Assert.False(PluginId.TablePrefix("ai").StartsWith(PluginId.TablePrefix("ai-tools"), StringComparison.Ordinal));
    }

    [Fact]
    public void Refusal_SaysWhy()
    {
        Assert.Null(PluginVersions.Refusal(M("kb.fits") with { AbstractionsVersion = KbHost.AbstractionsVersion, MinKbVersion = KbHost.Version, UiApiVersion = KbHost.UiApiVersion }));
        Assert.Null(PluginVersions.Refusal(M("kb.old") with { AbstractionsVersion = "0.1.0", MinKbVersion = "0.1.0" }));
        Assert.Equal("Built for a newer plugin API (1.0.0).", PluginVersions.Refusal(M("kb.new") with { AbstractionsVersion = "1.0.0" }));
        Assert.Equal("Needs version 9.0.0 or later.", PluginVersions.Refusal(M("kb.new") with { MinKbVersion = "9.0.0" }));
        Assert.Equal("Built for a newer plugin UI (2.0.0).", PluginVersions.Refusal(M("kb.new") with { UiApiVersion = "2.0.0" }));
        Assert.Equal("Version is not valid.", PluginVersions.Refusal(M("kb.bad", version: "one")));
    }

    [Fact]
    public void Order_PutsRequirementsFirst()
    {
        var plan = PluginGraph.Order([M("kb.test.dependent", requires: new PluginRequirement("kb.test.lib", "1.0.0")), M("kb.test.lib"), M("kb.alone")]);
        Assert.Equal(["kb.test.lib", "kb.test.dependent", "kb.alone"], plan.LoadOrder);
        Assert.Empty(plan.Refused);
    }

    [Fact]
    public void Order_MissingRequirement_Refused()
    {
        var plan = PluginGraph.Order([M("kb.test.dependent", requires: new PluginRequirement("kb.test.lib", "1.0.0"))]);
        Assert.Empty(plan.LoadOrder);
        Assert.Equal("Requires kb.test.lib.", plan.Refused["kb.test.dependent"]);
    }

    [Fact]
    public void Order_VersionTooLow_Refused()
    {
        var plan = PluginGraph.Order([M("kb.test.lib"), M("kb.test.dependent", requires: new PluginRequirement("kb.test.lib", "2.0.0"))]);
        Assert.Equal(["kb.test.lib"], plan.LoadOrder);
        Assert.Equal("Requires kb.test.lib 2.0.0 or later.", plan.Refused["kb.test.dependent"]);
    }

    [Fact]
    public void Order_Cycle_RefusesEveryPluginInIt()
    {
        var plan = PluginGraph.Order([
            M("kb.a", requires: new PluginRequirement("kb.b", "1.0.0")), M("kb.b", requires: new PluginRequirement("kb.c", "1.0.0")),
            M("kb.c", requires: new PluginRequirement("kb.a", "1.0.0")), M("kb.free"),
        ]);
        Assert.Equal(["kb.free"], plan.LoadOrder);
        Assert.Equal(["kb.a", "kb.b", "kb.c"], plan.Refused.Keys.Order());
        Assert.All(plan.Refused.Values, reason => Assert.Equal("Circular requirement.", reason));
    }

    [Fact]
    public void Order_SamePrefix_RefusesTheSecond()
    {
        var plan = PluginGraph.Order([M("a.b"), M("a-b")]);
        Assert.Equal(["a.b"], plan.LoadOrder);
        Assert.Equal("Conflicts with a.b.", plan.Refused["a-b"]);
    }

    [Fact]
    public void Order_RequirementThatIsRefused_RefusesItsDependents_ByName()
    {
        var plan = PluginGraph.Order([
            M("kb.test.lib", name: "Test library", requires: new PluginRequirement("kb.gone", "1.0.0")),
            M("kb.test.dependent", requires: new PluginRequirement("kb.test.lib", "1.0.0")),
        ]);
        Assert.Empty(plan.LoadOrder);
        Assert.Equal("Requires Test library.", plan.Refused["kb.test.dependent"]);
    }

    [Fact]
    public void Dependents_GoThroughEveryLevel()
    {
        PluginManifest[] all =
        [
            M("kb.lib"), M("kb.mid", requires: new PluginRequirement("kb.lib", "1.0.0")), M("kb.top", requires: new PluginRequirement("kb.mid", "1.0.0")), M("kb.other"),
        ];
        Assert.Equal(["kb.mid", "kb.top"], PluginGraph.Dependents("kb.lib", all).Order());
        Assert.Empty(PluginGraph.Dependents("kb.top", all));
    }

    [Fact]
    public void Missing_ListsWhatIsAbsentOrTooOld()
    {
        var m = M("kb.x", requires: [new PluginRequirement("kb.lib", "2.0.0"), new PluginRequirement("kb.gone", "1.0.0"), new PluginRequirement("kb.ok", "1.0.0")]);
        Assert.Equal(["kb.lib", "kb.gone"], PluginGraph.Missing(m, [M("kb.lib", "1.5.0"), M("kb.ok", "1.0.0")]).Select(r => r.Id));
    }

    [Fact]
    public void Manifest_OldFilesStillLoad()
    {
        var asm = typeof(SmtpPlugin).Assembly;
        using var stream = asm.GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith("kb-plugin.json", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        var m = PluginManifests.Parse(reader.ReadToEnd())!;

        Assert.Equal(("kb.email.smtp", "1.0.0"), (m.Id, m.Version));
        Assert.Contains(m.Settings, s => s.Key == "Password" && s.Secret && s.Type == "text" && !s.Restart);
        Assert.Empty(m.Requires);
        Assert.Empty(m.Permissions);
        Assert.False(m.Enforcing);
        Assert.Null(m.Ui);
        Assert.Null(PluginVersions.Refusal(m));
    }

    [Fact]
    public async Task Context_ReadsSettingsAsTheyAreNow_AndSecretsOnlyAfterStartup()
    {
        var settings = new PluginSettingsSnapshot();
        var context = new PluginContext(M("kb.x"), settings);
        Assert.Null(context.GetSetting("Greeting"));
        settings.Replace([("kb.x", "Greeting", "Hello"), ("kb.other", "Greeting", "Not mine")]);
        Assert.Equal("Hello", context.GetSetting("Greeting"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.GetSecretAsync("Token"));
        settings.SecretReader = (id, key, _) => Task.FromResult<string?>($"{id}/{key}");
        Assert.Equal("kb.x/Token", await context.GetSecretAsync("Token"));
    }

    [Fact]
    public void Outcome_HoldsAValueOrAProblem()
    {
        Outcome<int> ok = 5;
        Outcome<int> bad = Problem.Conflict("In use.");
        Assert.True(ok.Ok);
        Assert.Equal(5, ok.Value);
        Assert.False(bad.Ok);
        Assert.Equal((409, "In use."), (bad.Problem!.Status, bad.Problem.Message));
        Assert.Equal(["Required."], Problem.Invalid("name", "Required.").Fields!["name"]);
        Assert.Equal(422, Problem.Refused("No.").Status);
        Assert.Equal(423, Problem.Locked("Locked.").Status);
    }
}
