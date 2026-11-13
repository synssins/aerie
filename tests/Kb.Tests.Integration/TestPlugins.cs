using System.IO.Compression;
using System.Net;
using System.Reflection;
using Kb.Core.Plugins;
using Kb.Plugins;
using Kb.Web;

namespace Kb.Tests.Integration;

/// <summary>Records the restarts the app asked for. Tests restart the app themselves with <c>KbAppFactory.RestartAsync</c>.</summary>
public sealed class FakeRestarter : IAppRestarter
{
    private readonly List<string> _reasons = [];
    public IReadOnlyList<string> Reasons { get { lock (_reasons) return [.. _reasons]; } }
    public void Restart(string reason) { lock (_reasons) _reasons.Add(reason); }
}

/// <summary>The three test plugins as packages. Their output folders come from the build, so no test depends on a path typed by hand.</summary>
public static class TestPlugins
{
    public const string Probe = "Kb.TestPlugin.Probe", Lib = "Kb.TestPlugin.Lib", Dependent = "Kb.TestPlugin.Dependent";

    /// <summary>A zip of the plugin's built output, with its manifest at the top. <paramref name="change"/> edits the manifest.</summary>
    public static byte[] Package(string project, Func<PluginManifest, PluginManifest>? change = null)
    {
        var folder = typeof(TestPlugins).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TestPlugin:" + project).Value!;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                using var entry = zip.CreateEntry(name).Open();
                if (name == PluginLoader.ManifestFileName && change is not null)
                    entry.Write(System.Text.Encoding.UTF8.GetBytes(PluginManifests.Write(change(PluginManifests.Parse(File.ReadAllText(file))!))));
                else
                    entry.Write(File.ReadAllBytes(file));
            }
        }
        return ms.ToArray();
    }

    public static async Task InstallAsync(HttpClient admin, byte[] package)
    {
        using var content = new ByteArrayContent(package);
        var r = await admin.PostAsync("/api/v1/admin/plugins", content);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"Install answered {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }
}
