using Shouldly;
using Sightline.App.Services;
using SkiaSharp;
using Xunit;

namespace Sightline.App.Tests.Services;

/// <summary>Reading a frame's brightness for Sentry.</summary>
public sealed class LumaSamplerTests
{
    private static byte[] Jpeg(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    [Fact]
    public void A_frame_becomes_a_grid_a_sixteenth_of_its_size()
    {
        var grid = LumaSampler.Grid(Jpeg(640, 360, new SKColor(200, 200, 200)))!;

        grid.Width.ShouldBe(40);
        grid.Height.ShouldBe(23);
        grid.Cells.ShouldAllBe(cell => cell >= 195 && cell <= 205);
    }

    [Fact]
    public void Brightness_is_weighted_as_the_eye_weights_colour()
    {
        LumaSampler.Luma(new SKColor(255, 0, 0)).ShouldBe(76);
        LumaSampler.Luma(new SKColor(0, 255, 0)).ShouldBe(149);
        LumaSampler.Luma(new SKColor(0, 0, 255)).ShouldBe(29);
        LumaSampler.Luma(SKColors.White).ShouldBe(255);
    }

    [Fact]
    public void A_frame_that_does_not_decode_gives_nothing()
    {
        LumaSampler.Grid([0xFF, 0xD8, 0xFF, 0xD9]).ShouldBeNull();
        LumaSampler.Grid(Jpeg(640, 360, SKColors.Gray)[..200]).ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => LumaSampler.Grid(null!));
    }
}
