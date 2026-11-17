using Kb.Core.Plugins;
using Kb.Plugins;

namespace Kb.Tests.Unit;

/// <summary>A setting's help, default, unit, bounds and option labels (contract 0.7.0): read from the manifest, checked, and used.</summary>
public class PluginSettingFieldsTests
{
    private static PluginManifest With(params PluginSetting[] settings) =>
        new() { Id = "kb.x", Version = "1.0.0", Publisher = "test", MinKbVersion = "0.1.0", AbstractionsVersion = "0.6.0", Settings = settings };

    [Fact]
    public void A_0_6_0_Setting_ReadsAsBefore_AndTheNewFieldsAreRead()
    {
        var m = PluginManifests.Parse("""
            {
              "id": "kb.x", "version": "1.0.0", "publisher": "p", "minKbVersion": "0.6.0", "abstractionsVersion": "0.6.0",
              "settings": [
                { "key": "Old", "label": "Old (empty: 50)", "type": "number" },
                { "key": "Size", "label": "Largest file", "type": "number", "help": "Larger files are refused.", "default": "50", "unit": "MB", "min": 1, "max": 500 },
                { "key": "Mode", "label": "Mode", "type": "select", "options": ["a", "b"], "optionLabels": { "a": "Fast", "b": "Careful" } }
              ]
            }
            """)!;
        var old = m.Settings[0];
        Assert.Equal((null, null, null, null, null, null), (old.Help, old.Default, old.Unit, old.Min, old.Max, old.OptionLabels));
        var size = m.Settings[1];
        Assert.Equal(("Larger files are refused.", "50", "MB", 1m, 500m), (size.Help, size.Default, size.Unit, size.Min, size.Max));
        Assert.Equal("Careful", m.Settings[2].OptionLabels!["b"]);
        Assert.Null(PluginVersions.Refusal(m));
    }

    [Fact]
    public void ASettingThatCouldNeverBeRight_IsRefusedAtInstall()
    {
        Assert.Equal("Setting S: min is more than max.", PluginVersions.Refusal(With(new PluginSetting("S", "S", Type: "number", Min: 5, Max: 1))));
        Assert.Equal("Setting S: min and max are for a number.", PluginVersions.Refusal(With(new PluginSetting("S", "S", Min: 1))));
        Assert.Equal("Setting S: the default is refused: At most 10 MB.", PluginVersions.Refusal(With(new PluginSetting("S", "S", Type: "number", Default: "20", Unit: "MB", Max: 10))));
        Assert.Equal("Setting S: the default is refused: Not one of the choices.", PluginVersions.Refusal(With(new PluginSetting("S", "S", Type: "select", Options: ["a"], Default: "z"))));
        Assert.Equal("Setting S: a secret has no default.", PluginVersions.Refusal(With(new PluginSetting("S", "S", Secret: true, Default: "x"))));
        Assert.Equal("Setting S: optionLabels names \"z\", which is not one of the options.",
            PluginVersions.Refusal(With(new PluginSetting("S", "S", Type: "select", Options: ["a"], OptionLabels: new Dictionary<string, string> { ["z"] = "Z" }))));
    }

    [Theory]
    [InlineData("5", null)]
    [InlineData("0.5", "At least 1 MB.")]
    [InlineData("501", "At most 500 MB.")]
    [InlineData("many", "A number.")]
    public void ANumber_IsCheckedAgainstItsBounds_InWordsThatNameTheUnit(string value, string? refusal) =>
        Assert.Equal(refusal, SettingValue.Refusal(new PluginSetting("S", "S", Type: "number", Unit: "MB", Min: 1, Max: 500), value));

    [Fact]
    public void ThePlugin_GetsTheDefault_WhileNothingIsSaved_AndTheSavedValueOnceThereIsOne()
    {
        var snapshot = new PluginSettingsSnapshot();
        var context = new PluginContext(With(new PluginSetting("Size", "Size", Type: "number", Default: "50"), new PluginSetting("Key", "Key", Secret: true), new PluginSetting("Plain", "Plain")), snapshot);
        Assert.Equal(("50", null, null), (context.GetSetting("Size"), context.GetSetting("Key"), context.GetSetting("Plain")));
        snapshot.Replace([("kb.x", "Size", "75")]);
        Assert.Equal("75", context.GetSetting("Size"));
    }
}
