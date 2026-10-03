using BenchmarkDotNet.Attributes;
using Sightline.Benchmarks.Camera;
using SkiaSharp;

namespace Sightline.Benchmarks;

/// <summary>
/// Decoding a frame for the Windows live view, the way Avalonia does it.
/// </summary>
/// <remarks>
/// <para>
/// The Windows app turns every frame into a bitmap with <c>new Bitmap(stream)</c>, which Avalonia
/// 12 hands to SkiaSharp as SKManagedStream, SKData.Create, SKBitmap.Decode, SetImmutable and
/// SKImage.FromBitmap. <see cref="DecodeFrame"/> makes exactly those calls, so its cost is the app's
/// cost for each frame. <see cref="DecodeFrameAtHalfSize"/> makes the calls behind
/// <c>Bitmap.DecodeToWidth</c> instead, where the JPEG decoder scales by half as it goes: what a
/// smaller live view or a strip of thumbnails could use.
/// </para>
/// <para>
/// Allocated counts managed memory only. The decoded pixels, about 900 KB a frame at full size, are
/// Skia's native memory and appear in no figure here.
/// </para>
/// </remarks>
public class LiveViewDecode
{
    private byte[] jpeg = [];

    [GlobalSetup]
    public void Setup()
    {
        jpeg = SyntheticVideo.Pictures(1)[0];

        Expect.Equal(new SKSizeI(SyntheticVideo.Width, SyntheticVideo.Height), DecodeFrame(), "the decoded size");

        // Exactly half, so Avalonia would not resize afterwards and these calls are all of its work.
        Expect.Equal(
            new SKSizeI(SyntheticVideo.Width / 2, SyntheticVideo.Height / 2),
            DecodeFrameAtHalfSize(),
            "the size decoded at half scale");
    }

    /// <summary>One frame decoded at full size: the Windows live view today.</summary>
    [Benchmark]
    public SKSizeI DecodeFrame()
    {
        using var stream = new MemoryStream(jpeg);
        using var skiaStream = new SKManagedStream(stream);
        using var data = SKData.Create(skiaStream);
        using var bitmap = SKBitmap.Decode(data);
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        return new SKSizeI(image.Width, image.Height);
    }

    /// <summary>One frame decoded at half size by the JPEG decoder itself.</summary>
    [Benchmark]
    public SKSizeI DecodeFrameAtHalfSize()
    {
        using var stream = new MemoryStream(jpeg);
        using var skiaStream = new SKManagedStream(stream);
        using var data = SKData.Create(skiaStream);
        using var codec = SKCodec.Create(data);
        var half = codec.GetScaledDimensions(0.5f);
        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(half.Width, half.Height));
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        return new SKSizeI(image.Width, image.Height);
    }
}
