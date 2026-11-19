using Kb.Core.Ai;
using SkiaSharp;

namespace Kb.Web.Import;

/// <summary>
/// The four quarters of a picture, enlarged and overlapping a little, for the diagram request: a small model reads the labels of a big
/// whiteboard far better when it also sees them closer. The decoder is native code, where a crash takes the whole process down, so every
/// picture is treated as hostile: its size is read from its header in managed code first, and a picture above the caps is never decoded.
/// Anything that goes wrong gives null, and the request goes with the whole picture alone, as without quarters.
/// </summary>
public static class PictureQuarters
{
    /// <summary>
    /// The file bytes a picture may have: the importer sends no bigger picture to the AI anyway. The pixels it may have: 24 million (a
    /// 6000 by 4000 photo) at most, 12,000 a side, because a decoded picture takes 4 bytes a pixel whatever its file size, and a small file
    /// can claim a huge size (a decompression bomb). Below 64 a side a quarter shows nothing the whole picture does not.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024, MaxSide = 12_000, MinSide = 64;
    public const long MaxPixels = 24_000_000;

    /// <summary>What each quarter reaches over the middle, as a share of the picture's side: a box on the middle line is whole in one.</summary>
    private const double Overlap = 0.075;

    /// <summary>Whether the library loaded on this machine. Set by <see cref="SelfTest"/> at the start; off, no picture is cut.</summary>
    public static bool Available { get; private set; }

    private static readonly Lock Gate = new();
    private static bool tested;

    /// <summary>
    /// Makes, encodes and decodes a tiny picture, at the start. A library that cannot load (a native file missing for this machine) is
    /// logged and turns the quarters off; it never stops the start. Once a process: the native part loads once.
    /// </summary>
    public static bool SelfTest(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (Gate)
        {
            if (!tested) Test(log);
            tested = true;
            return Available;
        }
    }

    private static void Test(ILogger log)
    {
        try
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.White);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var back = SKBitmap.Decode(png.ToArray());
            Available = back is { Width: 2, Height: 2 };
            if (!Available) log.LogWarning("The image library did not read back its own test picture: diagrams are read from the whole picture only.");
        }
        catch (Exception e)
        {
            Available = false;
            log.LogWarning(e, "The image library could not be loaded: diagrams are read from the whole picture only.");
        }
    }

    /// <summary>
    /// The four quarters (top left, top right, bottom left, bottom right) as JPEG, or null: the library is off, the picture is not PNG,
    /// JPEG, GIF or WebP, is above the caps or too small, or could not be read.
    /// </summary>
    public static IReadOnlyList<AiGuard.Picture>? Cut(AiGuard.Picture picture, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(log);
        var d = picture.Data;
        if (!Available || d.Length > MaxBytes || Size(d) is not var (w, h) || !Fits(w, h)) return null;
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(d));
            // The decoder's own reading of the header must agree with ours: a file that says one size to us and another to it is not cut.
            if (codec is null || codec.Info.Width != w || codec.Info.Height != h) return null;
            // Decoded whole or not at all: a decoder that stops early (a cut or damaged file) is not cut into quarters of half a picture.
            using var whole = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (codec.GetPixels(whole.Info, whole.GetPixels()) != SKCodecResult.Success) return null;
            var (ox, oy) = ((int)(w * Overlap), (int)(h * Overlap));
            var quarters = new List<AiGuard.Picture>(4);
            foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                var area = new SKRectI(Math.Max(0, x * w / 2 - ox), Math.Max(0, y * h / 2 - oy), Math.Min(w, (x + 1) * w / 2 + ox), Math.Min(h, (y + 1) * h / 2 + oy));
                using var part = new SKBitmap();
                if (!whole.ExtractSubset(part, area)) return null;
                using var image = SKImage.FromBitmap(part);
                using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                if (jpeg is null) return null;
                quarters.Add(new AiGuard.Picture("image/jpeg", jpeg.ToArray()));
            }
            return quarters;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "A picture could not be cut into quarters; its diagram is read from the whole picture.");
            return null;
        }
    }

    private static bool Fits(int w, int h) => w is >= MinSide and <= MaxSide && h is >= MinSide and <= MaxSide && (long)w * h <= MaxPixels;

    /// <summary>The size from the header: PNG, JPEG and GIF as the exports read it, and WebP (lossy, lossless or extended) here.</summary>
    public static (int Width, int Height)? Size(byte[] d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Length >= 30 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        {
            var chunk = System.Text.Encoding.ASCII.GetString(d, 12, 4);
            return chunk switch
            {
                // Lossy: after the frame tag and its start code, 14 bits each.
                "VP8 " when d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A => ((d[26] | (d[27] << 8)) & 0x3FFF, (d[28] | (d[29] << 8)) & 0x3FFF),
                // Lossless: after its signature byte, 14 bits each, less one.
                "VP8L" when d[20] == 0x2F => (1 + (d[21] | ((d[22] & 0x3F) << 8)), 1 + ((d[22] >> 6) | (d[23] << 2) | ((d[24] & 0x0F) << 10))),
                // Extended: the canvas, 24 bits each, less one.
                "VP8X" => (1 + (d[24] | (d[25] << 8) | (d[26] << 16)), 1 + (d[27] | (d[28] << 8) | (d[29] << 16))),
                _ => null,
            };
        }
        return Kb.Web.Exports.ImageSize(d);
    }
}
