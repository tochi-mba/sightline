namespace Sightline.Protocol;

/// <summary>
/// The zeros the reference camera pads every JPEG with, in its stream and in its clips alike: up to seven after
/// the end-of-image marker, to a multiple of eight bytes.
/// </summary>
public static class JpegPadding
{
    /// <summary>
    /// How long <paramref name="picture"/> is without the zeros that follow its end-of-image marker. Zeros with
    /// no marker before them are counted in: they could be the picture's own, and a picture with no end is
    /// judged on its own.
    /// </summary>
    public static int End(ReadOnlySpan<byte> picture)
    {
        var end = picture.Length;
        while (end > 0 && picture[end - 1] == 0)
        {
            end--;
        }

        return end >= 2 && picture[end - 2] == 0xFF && picture[end - 1] == 0xD9 ? end : picture.Length;
    }
}
