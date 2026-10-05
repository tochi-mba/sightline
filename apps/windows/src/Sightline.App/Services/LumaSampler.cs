using Sightline.Core.Sentry;
using SkiaSharp;

namespace Sightline.App.Services;

/// <summary>
/// Turns a live-view JPEG into the small brightness grid Sentry looks at: a sixteenth of the frame on each
/// side, so a 640 by 360 picture becomes 40 by 23, as on the phone.
/// </summary>
public static class LumaSampler
{
    /// <summary>How much smaller than the frame the grid is, on each side.</summary>
    public const int Sample = 16;

    /// <summary>The grid for <paramref name="jpeg"/>, or null when it does not decode.</summary>
    public static LumaGrid? Grid(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        using var data = SKData.CreateCopy(jpeg);
        // Bytes Skia does not recognise give no codec, rather than an exception from decoding.
        using var codec = SKCodec.Create(data);
        using var full = codec is null ? null : SKBitmap.Decode(codec);
        if (full is null)
        {
            return null;
        }

        var width = (full.Width + Sample - 1) / Sample;
        var height = (full.Height + Sample - 1) / Sample;
        using var small = full.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        var cells = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                cells[(y * width) + x] = Luma(small.GetPixel(x, y));
            }
        }

        return new LumaGrid(width, height, cells);
    }

    /// <summary>A colour's brightness, 0 to 255, weighted as the eye weights red, green and blue.</summary>
    public static int Luma(SKColor colour) => ((299 * colour.Red) + (587 * colour.Green) + (114 * colour.Blue)) / 1000;
}
