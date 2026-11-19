using Kb.Core.Ai;
using Kb.Web.Import;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Kb.Tests.Integration;

/// <summary>The enlarged quarters of a picture for the diagram request, and the pictures that are never cut.</summary>
public sealed class PictureQuartersTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    public PictureQuartersTests() => Assert.True(PictureQuarters.SelfTest(Log), "the image library loads on this machine");

    /// <summary>A picture of the given size and format, with a frame drawn on it.</summary>
    public static byte[] Picture(int w, int h, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var pen = new SKPaint { Color = SKColors.Black, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            canvas.DrawRect(10, 10, w - 20, h - 20, pen);
        }
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void APicture_IsCutIntoFourOverlappingQuarters(SKEncodedImageFormat format, string mime)
    {
        var d = Picture(400, 300, format);
        Assert.Equal((400, 300), PictureQuarters.Size(d));
        var quarters = PictureQuarters.Cut(new AiGuard.Picture(mime, d), Log);
        Assert.NotNull(quarters);
        Assert.Equal(4, quarters.Count);
        foreach (var q in quarters)
        {
            Assert.Equal("image/jpeg", q.Mime);
            using var b = SKBitmap.Decode(q.Data);
            // Half of each side, and 7.5 percent of it over the middle.
            Assert.Equal((230, 172), (b.Width, b.Height));
        }
    }

    [Fact]
    public void APictureThatClaimsTooManyPixels_IsNotDecoded()
    {
        // A small PNG whose header says 20,000 by 20,000: 1.6 GB once decoded.
        var d = Picture(100, 100, SKEncodedImageFormat.Png);
        d[16] = 0; d[17] = 0; d[18] = 0x4E; d[19] = 0x20;
        d[20] = 0; d[21] = 0; d[22] = 0x4E; d[23] = 0x20;
        Assert.Equal((20_000, 20_000), PictureQuarters.Size(d));
        Assert.Null(PictureQuarters.Cut(new AiGuard.Picture("image/png", d), Log));
    }

    [Fact]
    public void APictureOfTooManyBytes_IsNotCut()
    {
        var d = Picture(400, 300, SKEncodedImageFormat.Png);
        var big = new byte[PictureQuarters.MaxBytes + 1];
        d.CopyTo(big, 0);
        Assert.Null(PictureQuarters.Cut(new AiGuard.Picture("image/png", big), Log));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void ACorruptPicture_IsNotCut_AndThrowsNothing(SKEncodedImageFormat format)
    {
        // Its header is whole; what follows is noise.
        var d = Picture(400, 300, format);
        new Random(7).NextBytes(d.AsSpan(40));
        Assert.Null(PictureQuarters.Cut(new AiGuard.Picture("image/png", d), Log));
    }

    [Fact]
    public void ATinyPicture_OrNoPictureAtAll_IsNotCut()
    {
        Assert.Null(PictureQuarters.Cut(new AiGuard.Picture("image/png", Picture(40, 40, SKEncodedImageFormat.Png)), Log));
        Assert.Null(PictureQuarters.Cut(new AiGuard.Picture("image/png", "not a picture"u8.ToArray()), Log));
    }
}
