using System.Buffers.Binary;
using Sightline.Protocol.Rtp;
using SkiaSharp;

namespace Sightline.Benchmarks.Camera;

/// <summary>
/// A live stream shaped like the reference camera's, made from nothing.
/// </summary>
/// <remarks>
/// <para>
/// A real capture shows somebody's room and carries the camera's MAC address, so none is ever
/// committed. This builds a stand-in with the same shape each time a benchmark needs one: 640 by
/// 360 JPEG at 12.2 frames a second and about 11 KB a frame, sent as bare RTP with no interleaved
/// framing from the fixed source <c>0x22222222</c>, in packets of about 1.4 KB. Those are the
/// figures docs/PROTOCOL.md measured on the reference camera.
/// </para>
/// <para>
/// The pictures are real JPEGs that SkiaSharp encodes from a generated room with some grain in it,
/// so their bytes look like compressed picture data. That matters to the reassembler, which hunts
/// for packet headers a byte at a time: a payload of zeros or a repeated pattern would make the
/// hunt look cheaper than it is on a real stream.
/// </para>
/// </remarks>
internal static class SyntheticVideo
{
    /// <summary>Pixels across, as the reference camera streams.</summary>
    public const int Width = 640;

    /// <summary>Pixels down.</summary>
    public const int Height = 360;

    /// <summary>The largest a picture may be. The encoder gets the highest quality that fits.</summary>
    public const int PictureBytes = 11_000;

    /// <summary>One second of distinct pictures; a longer stream goes round them again.</summary>
    public const int DistinctPictures = 12;

    private const double FramesPerSecond = 12.2;
    private const int RtpClock = 90_000;
    private const uint Source = 0x22222222;

    /// <summary>A whole packet, headers included.</summary>
    private const int PacketBytes = 1_400;

    /// <summary>The RTP header, then RFC 2435's JPEG header.</summary>
    private const int HeaderBytes = 12 + 8;

    /// <summary>
    /// The pictures of a stream, each a whole JPEG of at most <see cref="PictureBytes"/>.
    /// </summary>
    /// <remarks>
    /// One quality serves every picture, found on the first, as a camera's encoder keeps one
    /// setting while the scene moves.
    /// </remarks>
    public static byte[][] Pictures(int count)
    {
        int quality;
        using (var first = Scene(0))
        {
            quality = HighestQualityWithin(first, PictureBytes);
        }

        var pictures = new byte[count][];
        for (var picture = 0; picture < count; picture++)
        {
            using var scene = Scene(picture);
            pictures[picture] = Encode(scene, quality);
        }

        return pictures;
    }

    /// <summary>
    /// What the camera sends after PLAY: <paramref name="frames"/> pictures in RTP packets.
    /// </summary>
    /// <param name="pictures">The pictures to send, in turn.</param>
    /// <param name="frames">How many frames, going round <paramref name="pictures"/> as often as it takes.</param>
    public static byte[] Stream(byte[][] pictures, int frames)
    {
        using var wire = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderBytes];
        ushort sequence = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var jpeg = pictures[frame % pictures.Length];
            var timestamp = (uint)Math.Round(frame * RtpClock / FramesPerSecond);
            for (var offset = 0; offset < jpeg.Length; offset += PacketBytes - HeaderBytes)
            {
                var size = Math.Min(PacketBytes - HeaderBytes, jpeg.Length - offset);
                WriteHeader(header, sequence++, timestamp, offset, last: offset + size == jpeg.Length);
                wire.Write(header);
                wire.Write(jpeg, offset, size);
            }
        }

        return wire.ToArray();
    }

    private static void WriteHeader(Span<byte> header, ushort sequence, uint timestamp, int fragmentOffset, bool last)
    {
        // RTP: version 2 with nothing optional, the JPEG payload type with the marker bit on a
        // picture's last packet, then the sequence, the 90 kHz timestamp and the fixed source.
        header[0] = 0x80;
        header[1] = (byte)(RtpJpegReassembler.JpegPayloadType | (last ? 0x80 : 0x00));
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], Source);

        // RFC 2435: type-specific, a 24-bit fragment offset, the type, a Q below 128 so no tables
        // follow, and the size in eight-pixel units. The same fields the reassembler's tests use.
        header[12] = 0;
        header[13] = (byte)(fragmentOffset >> 16);
        header[14] = (byte)(fragmentOffset >> 8);
        header[15] = (byte)fragmentOffset;
        header[16] = 1;
        header[17] = 1;
        header[18] = Width / 8;
        header[19] = Height / 8;
    }

    private static int HighestQualityWithin(SKBitmap scene, int bytes)
    {
        // A higher quality makes a larger file, so a binary search finds the best one that fits.
        var low = 1;
        var high = 100;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encode(scene, middle).Length <= bytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static byte[] Encode(SKBitmap scene, int quality)
    {
        using var data = scene.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>
    /// A room: a wall lit from one side with a window in it, a cupboard, a tiled floor, something
    /// moving across it and the camera's date stamp, with a little sensor grain over everything.
    /// </summary>
    /// <remarks>
    /// Computed pixel by pixel from integers rather than drawn with Skia's canvas, so the scene does
    /// not depend on how Skia rasterises shapes. The grain sets how hard the picture is to compress:
    /// at this level the camera's 11 KB comes out near quality 60.
    /// </remarks>
    private static SKBitmap Scene(int picture)
    {
        var scene = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = scene.GetPixelSpan();
        var grain = new Grain(picture);
        var moving = 40 + (picture * 37 % (Width - 160));
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = Room(x, y, picture, moving);
                var noise = grain.Next();
                var at = ((y * Width) + x) * 4;
                pixels[at] = Clamp(r + noise);
                pixels[at + 1] = Clamp(g + noise);
                pixels[at + 2] = Clamp(b + noise);
                pixels[at + 3] = 255;
            }
        }

        return scene;
    }

    private static (int R, int G, int B) Room(int x, int y, int picture, int moving)
    {
        if (x >= moving && x < moving + 60 && y is >= 250 and < 310)
        {
            return (40, 90, 160);
        }

        if (x is >= 440 and < 632 && y is >= 330 and < 352)
        {
            return DateStamp(x - 440, y - 330, picture);
        }

        if (x is >= 70 and < 230 && y is >= 40 and < 170)
        {
            // The window, with a frame across it.
            return x is >= 146 and < 154 || y is >= 101 and < 109 ? (60, 55, 50) : (225, 232, 240);
        }

        if (x is >= 380 and < 560 && y is >= 150 and < 300)
        {
            // The cupboard, with a handle.
            return x is >= 466 and < 474 && y is >= 200 and < 240
                ? (200, 200, 190)
                : (95 - ((y - 150) / 10), 60, 40);
        }

        if (y < 216)
        {
            return (190 - (x / 8) - (y / 6), 180 - (x / 9) - (y / 6), 160 - (x / 10) - (y / 7));
        }

        // The floor's tiles give the encoder edges to spend bytes on.
        var tile = (((x / 40) + (y / 24)) & 1) == 0 ? 18 : 0;
        var depth = y - 216;
        return (120 + tile - (depth / 6), 95 + tile - (depth / 7), 70 + tile - (depth / 8));
    }

    /// <summary>
    /// The clock the camera burns into its picture: hard white strokes on black that change once a
    /// second. Not real digits, only the edges a burnt-in clock gives the encoder.
    /// </summary>
    private static (int R, int G, int B) DateStamp(int x, int y, int picture)
    {
        var cell = x / 12;
        var stroke = x % 12 < 3 || y % 11 < 2;
        var lit = ((((cell * 7) + (cell * cell) + (picture / DistinctPictures)) >> (y / 6)) & 1) == 1;
        return stroke && lit && cell % 3 != 2 ? (250, 250, 250) : (10, 10, 10);
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>Sensor-like grain from xorshift: the same for the same picture on every run.</summary>
    private struct Grain(int picture)
    {
        private const int Amplitude = 6;

        private uint state = 0x9E3779B9u ^ ((uint)picture * 0x85EBCA6Bu);

        public int Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % ((Amplitude * 2) + 1u)) - Amplitude;
        }
    }
}
