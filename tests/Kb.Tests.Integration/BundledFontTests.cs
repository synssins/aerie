using Kb.Web;

namespace Kb.Tests.Integration;

/// <summary>
/// PDF export brings its own fonts (Liberation Sans and Mono, built into the app), so it works the same on Windows, IIS and in
/// the Docker image, with no font installed. They take the same space as Arial and Courier New.
/// </summary>
public sealed class BundledFontTests
{
    [Theory]
    [InlineData("Arial", false, false, "LiberationSans-Regular")]
    [InlineData("Arial", true, false, "LiberationSans-Bold")]
    [InlineData("Arial", false, true, "LiberationSans-Italic")]
    [InlineData("Arial", true, true, "LiberationSans-BoldItalic")]
    [InlineData("Courier New", false, false, "LiberationMono-Regular")]
    [InlineData("Consolas", true, false, "LiberationMono-Bold")]
    [InlineData("Something Else", false, false, "LiberationSans-Regular")]
    public void AFamily_IsMappedToABundledFace(string family, bool bold, bool italic, string face) =>
        Assert.Equal(face, new BundledFontResolver().ResolveTypeface(family, bold, italic)!.FaceName);

    [Theory]
    [InlineData("LiberationSans-Regular")]
    [InlineData("LiberationSans-Bold")]
    [InlineData("LiberationSans-Italic")]
    [InlineData("LiberationSans-BoldItalic")]
    [InlineData("LiberationMono-Regular")]
    [InlineData("LiberationMono-Bold")]
    [InlineData("LiberationMono-Italic")]
    [InlineData("LiberationMono-BoldItalic")]
    public void EveryFace_IsInTheApp(string face)
    {
        var data = new BundledFontResolver().GetFont(face);
        Assert.NotNull(data);
        Assert.True(data.Length > 100_000, $"{face} is {data.Length} bytes");
    }

    [Fact]
    public void AnUnknownFace_IsNull() => Assert.Null(new BundledFontResolver().GetFont("NoSuchFont-Regular"));
}
