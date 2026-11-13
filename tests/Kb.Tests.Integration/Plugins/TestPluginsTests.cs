using System.IO.Compression;
using System.Net.Http.Json;
using Kb.Core.Plugins;
using Kb.Web;

namespace Kb.Tests.Integration.Plugins;

public sealed class TestPluginsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Package_ContainsManifestAndAssembly()
    {
        using var zip = new ZipArchive(new MemoryStream(TestPlugins.Package(TestPlugins.Probe, m => m with { Version = "2.5.0" })));
        Assert.Contains(zip.Entries, e => e.FullName == "Kb.TestPlugin.Probe.dll");
        using var reader = new StreamReader(zip.GetEntry("kb-plugin.json")!.Open());
        var manifest = PluginManifests.Parse(reader.ReadToEnd())!;
        Assert.Equal(("kb.test.probe", "Test probe", "2.5.0"), (manifest.Id, manifest.Name, manifest.Version));

        using var dependent = new ZipArchive(new MemoryStream(TestPlugins.Package(TestPlugins.Dependent)));
        Assert.Contains(dependent.Entries, e => e.FullName == "Kb.TestPlugin.Dependent.dll");
    }

    [Fact]
    public async Task RestartAsync_KeepsTheDatabase()
    {
        var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Before the restart" }, Ct);

        f = await f.RestartAsync();
        await using (f)
        {
            var spaces = await (await f.SignedInBreakGlassAsync()).GetFromJsonAsync<List<SpaceDto>>("/api/v1/spaces", Ct);
            Assert.Equal("Before the restart", Assert.Single(spaces!).Name);
            Assert.Empty(f.Restarter.Reasons);

            await using var second = f.SecondInstance();
            Assert.Equal(f.DbPath, second.DbPath);
            Assert.NotEqual(f.WorkFolder, second.WorkFolder);
        }
    }
}
