using Kb.Web;

namespace Kb.Tests.Integration;

/// <summary>On Linux (the Docker image) PDF export finds its fonts in the Liberation family, which matches Arial and Courier New in size.</summary>
public sealed class LinuxFontTests
{
    [Theory]
    [InlineData("Arial", false, false, "LiberationSans-Regular")]
    [InlineData("Arial", true, false, "LiberationSans-Bold")]
    [InlineData("Arial", false, true, "LiberationSans-Italic")]
    [InlineData("Arial", true, true, "LiberationSans-BoldItalic")]
    [InlineData("Courier New", false, false, "LiberationMono-Regular")]
    [InlineData("Consolas", true, false, "LiberationMono-Bold")]
    [InlineData("Something Else", false, false, "LiberationSans-Regular")]
    public void AFamily_IsMappedToAnInstalledFace(string family, bool bold, bool italic, string face) =>
        Assert.Equal(face, new LinuxFontResolver([]).ResolveTypeface(family, bold, italic)!.FaceName);

    [Fact]
    public void AFace_IsReadFromTheFontFolder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(dir, "truetype", "liberation"));
        File.WriteAllBytes(Path.Combine(dir, "truetype", "liberation", "LiberationSans-Regular.ttf"), [1, 2, 3]);
        Assert.Equal([1, 2, 3], new LinuxFontResolver([dir]).GetFont("LiberationSans-Regular"));
        Assert.Null(new LinuxFontResolver([dir]).GetFont("LiberationMono-Bold"));
    }
}
