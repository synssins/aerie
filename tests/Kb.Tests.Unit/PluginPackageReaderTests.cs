using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Kb.Core.Plugins;

namespace Kb.Tests.Unit;

public class PluginPackageReaderTests
{
    private const string Manifest = """{"id":"kb.test.probe","name":"Test probe","version":"1.0.0","publisher":"t","assembly":"Kb.TestPlugin.Probe.dll","abstractionsVersion":"0.2.0"}""";

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static byte[] Package(string manifest = Manifest, params (string Name, byte[] Content)[] more) =>
        Zip([("kb-plugin.json", Encoding.UTF8.GetBytes(manifest)), ("Kb.TestPlugin.Probe.dll", [1, 2, 3]), .. more]);

    private static (int Status, string? Message) Refusal(byte[] package)
    {
        var r = PluginPackageReader.Read(package);
        Assert.False(r.Ok);
        return (r.Problem!.Status, r.Problem.Message);
    }

    [Fact]
    public void Read_ValidPackage_ReturnsManifestAndHash()
    {
        var package = Package();
        var r = PluginPackageReader.Read(package);
        Assert.True(r.Ok);
        Assert.Equal(("kb.test.probe", "Test probe"), (r.Value!.Manifest.Id, r.Value.Manifest.Name));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(package)), r.Value.Sha256);
    }

    [Fact]
    public void Read_NotAZip_OrNoManifest_Refused()
    {
        Assert.Equal((400, "Not a plugin package."), Refusal([1, 2, 3, 4]));
        Assert.Equal((400, "Not a plugin package."), Refusal(Zip(("readme.txt", [1]))));
        Assert.Equal((400, "Not a plugin package."), Refusal(Zip(("kb-plugin.json", "not json"u8.ToArray()))));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("C:/drive.dll")]
    [InlineData("sub\\..\\..\\evil.dll")]
    public void Read_ZipSlip_Refused(string entry) => Assert.Equal((400, "Not a plugin package."), Refusal(Package(more: (entry, [1]))));

    [Fact]
    public void Read_TooManyEntriesOrTooLarge_Refused()
    {
        var many = Enumerable.Range(0, PluginPackageReader.MaxEntries).Select(i => ($"f{i}.txt", new byte[] { 1 })).ToArray();
        Assert.Equal((400, "Package too large."), Refusal(Package(more: many)));
        Assert.Equal((400, "Package too large."), Refusal(Package(more: ("big.bin", new byte[PluginPackageReader.MaxUnpackedBytes + 1]))));
    }

    [Fact]
    public void Read_InvalidId_Refused() =>
        Assert.Equal((400, "Plugin id is not valid."), Refusal(Package(Manifest.Replace("kb.test.probe", "Kb_Probe", StringComparison.Ordinal))));

    [Fact]
    public void Read_InvalidVersion_Refused()
    {
        Assert.Equal((400, "Version is not valid."), Refusal(Package(Manifest.Replace("\"1.0.0\"", "\"1.0\"", StringComparison.Ordinal))));
        Assert.Equal((400, "Version is not valid."), Refusal(Package(Manifest.Replace("\"1.0.0\"", "\"../1.0.0\"", StringComparison.Ordinal))));
    }

    [Fact]
    public void Read_MainAssemblyMissing_Refused() =>
        Assert.Equal((400, "Missing Kb.TestPlugin.Probe.dll."), Refusal(Zip(("kb-plugin.json", Encoding.UTF8.GetBytes(Manifest)))));

    [Fact]
    public void Read_AssemblyOrExportNameWithAPath_Refused()
    {
        Assert.Equal((400, "Not a plugin package."), Refusal(Package(Manifest.Replace("Kb.TestPlugin.Probe.dll", "sub/Kb.TestPlugin.Probe.dll", StringComparison.Ordinal), ("sub/Kb.TestPlugin.Probe.dll", [1]))));
        Assert.Equal((400, "Not a plugin package."), Refusal(Package(Manifest.Replace("\"publisher\"", "\"exports\":[\"..\\\\Other\"],\"publisher\"", StringComparison.Ordinal))));
    }

    [Fact]
    public void Unpack_WritesEveryFile_AndStopsAtTheLimit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kb-unpack-" + Guid.NewGuid().ToString("N"));
        PluginPackageReader.Unpack(Package(more: ("ui/index.js", "x"u8.ToArray())), folder);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder, "Kb.TestPlugin.Probe.dll")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(folder, "ui", "index.js")));
        Assert.Throws<InvalidDataException>(() => PluginPackageReader.Unpack(Package(more: ("../evil.dll", [1])), folder + "-b"));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.dll")));
    }

    // Finding 1: JSON null in a manifest list reached code that assumed a list, and stopped the app at every start.
    [Theory]
    [InlineData("\"outboundHosts\":null")]
    [InlineData("\"settings\":null")]
    [InlineData("\"requires\":null")]
    [InlineData("\"exports\":null")]
    [InlineData("\"extensionPoints\":null")]
    [InlineData("\"permissions\":null")]
    [InlineData("\"schedules\":null")]
    [InlineData("\"settings\":[null]")]
    [InlineData("\"settings\":[{\"key\":null,\"label\":\"x\"}]")]
    [InlineData("\"settings\":[{\"key\":\"k\",\"label\":null}]")]
    [InlineData("\"requires\":[{\"id\":null,\"minVersion\":\"1.0.0\"}]")]
    [InlineData("\"exports\":[null]")]
    [InlineData("\"outboundHosts\":[null]")]
    [InlineData("\"publisher\":null")]
    public void Read_ManifestWithANullListOrItem_Refused(string member)
    {
        var manifest = member.StartsWith("\"publisher\"", StringComparison.Ordinal)
            ? Manifest.Replace("\"publisher\":\"t\"", member, StringComparison.Ordinal)
            : Manifest.Replace("\"publisher\"", member + ",\"publisher\"", StringComparison.Ordinal);
        Assert.Equal((400, "Not a plugin package."), Refusal(Package(manifest)));
    }

    // Finding 10: "$" matches before a final line feed.
    [Fact]
    public void Read_IdOrVersionEndingInALineFeed_Refused()
    {
        Assert.Equal((400, "Version is not valid."), Refusal(Package(Manifest.Replace("\"1.0.0\"", "\"1.0.0\\n\"", StringComparison.Ordinal))));
        Assert.Equal((400, "Plugin id is not valid."), Refusal(Package(Manifest.Replace("\"kb.test.probe\"", "\"kb.test.probe\\n\"", StringComparison.Ordinal))));
        Assert.False(PluginId.IsValid("kb.evil\n"));
    }

    // Finding 13: the count while writing is the guard against a zip that lies about its sizes.
    [Fact]
    public void Unpack_StopsWhenTheBytesWrittenPassTheLimit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kb-unpack-" + Guid.NewGuid().ToString("N"));
        var e = Assert.Throws<InvalidDataException>(() => PluginPackageReader.Unpack(Package(more: ("big.bin", new byte[5000])), folder, maxBytes: 4000));
        Assert.Equal("Package too large.", e.Message);
        PluginPackageReader.Unpack(Package(more: ("big.bin", new byte[5000])), folder + "-ok", maxBytes: 6000);
    }
}
