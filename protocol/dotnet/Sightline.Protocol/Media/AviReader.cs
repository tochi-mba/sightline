using System.Buffers.Binary;
using System.Text;

namespace Sightline.Protocol.Media;

/// <summary>What an AVI clip holds, from its headers.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="FramesPerSecond">How many pictures a second it plays at.</param>
/// <param name="TotalFrames">How many pictures it holds.</param>
/// <param name="Sound">Its sound, or null when it has none.</param>
public sealed record AviClip(int Width, int Height, double FramesPerSecond, int TotalFrames, AviSound? Sound)
{
    /// <summary>How long it plays for.</summary>
    public TimeSpan Duration => TimeOf(TotalFrames);

    /// <summary>When picture <paramref name="number"/>, counted from 0, is shown.</summary>
    public TimeSpan TimeOf(int number) => FramesPerSecond > 0 ? TimeSpan.FromSeconds(number / FramesPerSecond) : TimeSpan.Zero;
}

/// <summary>An AVI clip's sound: plain PCM, as the reference camera records it.</summary>
/// <param name="SampleRate">Samples a second.</param>
/// <param name="Channels">How many channels.</param>
/// <param name="BitsPerSample">Bits in each sample.</param>
public sealed record AviSound(int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// One piece of an AVI clip's content, in the order it is stored, with where in the file it lies: a player
/// that keeps a long clip on disk rather than in memory reads it back from there.
/// </summary>
/// <param name="Offset">Where its bytes start in the file.</param>
public abstract record AviChunk(long Offset)
{
    /// <summary>A picture: a whole JPEG, numbered from 0 in the order it is stored.</summary>
    public sealed record Picture(int Number, long Offset, byte[] Jpeg) : AviChunk(Offset);

    /// <summary>A run of sound, as PCM samples.</summary>
    public sealed record Sound(long Offset, byte[] Pcm) : AviChunk(Offset);
}

/// <summary>
/// Reads an AVI clip as its bytes arrive, so a clip can be shown while it is still coming off the card.
/// </summary>
/// <remarks>
/// <para>
/// The reference camera records Motion JPEG in AVI: every picture is a whole JPEG in a <c>00dc</c> chunk, and
/// its sound is 16-bit PCM in <c>01wb</c> chunks, inside the <c>movi</c> list. So nothing needs decoding to
/// play it but the JPEGs, which the apps already show for the live picture.
/// </para>
/// <para>
/// A picture comes out without the zeros the camera pads it with after its end, as in its stream; see
/// <see cref="JpegPadding"/>.
/// </para>
/// <para>
/// Bytes go in whatever their boundaries, and a chunk comes out once all of it has arrived. Chunks are read
/// in the order they are stored: the header list is read whole, <c>movi</c> and <c>rec</c> lists are
/// stepped into, a picture or sound of a stream the header described is kept, and everything else,
/// <c>idx1</c> and <c>JUNK</c> included, is passed over without being held. So a clip of any length is
/// read in the memory of its largest picture.
/// </para>
/// </remarks>
public sealed class AviReader
{
    private const int ChunkHeader = 8;
    private const int ListHeader = 12;

    // Bigger than any header list or picture this camera writes; a chunk claiming more is not believed.
    private const int LargestKept = 16 * 1024 * 1024;

    private readonly List<byte> buffer = [];
    private readonly Dictionary<string, bool> streams = [];
    private long skipping;
    private long consumed;
    private bool started;
    private int pictures;

    /// <summary>The clip's headers, once they have arrived.</summary>
    public AviClip? Clip { get; private set; }

    /// <summary>
    /// Takes the next bytes of the file, and returns every picture and run of sound they completed.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not an AVI, or a chunk in it cannot be right.</exception>
    public IReadOnlyList<AviChunk> Push(ReadOnlySpan<byte> bytes)
    {
        var chunks = new List<AviChunk>();
        while (!bytes.IsEmpty)
        {
            if (skipping > 0)
            {
                var skip = (int)Math.Min(skipping, bytes.Length);
                skipping -= skip;
                bytes = bytes[skip..];
                continue;
            }

            buffer.AddRange(bytes);
            bytes = [];
            while (Step(chunks))
            {
            }
        }

        return chunks;
    }

    /// <summary>Takes the next step the buffered bytes allow, returning false when it needs more of them.</summary>
    private bool Step(List<AviChunk> chunks)
    {
        if (!started)
        {
            if (buffer.Count < ListHeader)
            {
                return false;
            }

            if (Code(0) != "RIFF" || Code(8) != "AVI ")
            {
                throw new InvalidDataException("This is not an AVI file.");
            }

            Discard(ListHeader);
            started = true;
            return true;
        }

        if (buffer.Count < ChunkHeader || skipping > 0)
        {
            return false;
        }

        var code = Code(0);
        var length = Padded(BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4, 4)));
        if (code == "LIST")
        {
            if (buffer.Count < ListHeader)
            {
                return false;
            }

            switch (Code(ChunkHeader))
            {
                case "movi" or "rec ":
                    // The clip itself: its chunks are read one by one rather than kept whole.
                    Discard(ListHeader);
                    return true;
                case "hdrl":
                    return Keep(length, (_, body) => ReadHeaders(body.AsSpan(4)));
                default:
                    Discard(ChunkHeader + length);
                    return true;
            }
        }

        if (!streams.TryGetValue(code[..2], out var isVideo)
            || !(isVideo ? code[2..] is "dc" or "db" : code[2..] == "wb"))
        {
            Discard(ChunkHeader + length);
            return true;
        }

        var unpadded = (int)BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4, 4));
        return Keep(length, (offset, body) => chunks.Add(isVideo
            ? new AviChunk.Picture(pictures++, offset, body[..JpegPadding.End(body.AsSpan(0, unpadded))])
            : new AviChunk.Sound(offset, body[..unpadded])));
    }

    /// <summary>Hands over a chunk's body of <paramref name="length"/> bytes, and where it starts, once all of it is here.</summary>
    private bool Keep(long length, Action<long, byte[]> take)
    {
        if (length > LargestKept)
        {
            throw new InvalidDataException($"A chunk of {length} bytes is more than a clip of this camera holds.");
        }

        if (buffer.Count < ChunkHeader + length)
        {
            return false;
        }

        var body = Bytes(ChunkHeader, (int)length);
        var offset = consumed + ChunkHeader;
        Discard(ChunkHeader + length);
        take(offset, body);
        return true;
    }

    /// <summary>Reads the header list: the main header, then each stream's header and format.</summary>
    private void ReadHeaders(ReadOnlySpan<byte> hdrl)
    {
        int width = 0, height = 0, total = 0;
        var perSecond = 0.0;
        AviSound? sound = null;
        var stream = 0;
        foreach (var (code, body) in Children(hdrl))
        {
            if (code == "avih" && body.Length >= 40)
            {
                var micros = BinaryPrimitives.ReadUInt32LittleEndian(body);
                perSecond = micros > 0 ? 1_000_000.0 / micros : 0;
                total = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16));
                width = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32));
                height = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(36));
            }
            else if (code == "LIST" && body.Length >= 4 && Encoding.ASCII.GetString(body, 0, 4) == "strl")
            {
                var (video, rate, audio) = ReadStream(body.AsSpan(4));
                var number = $"{stream++:D2}";
                if (video)
                {
                    streams[number] = true;
                    perSecond = rate ?? perSecond;
                }
                else if (audio is not null)
                {
                    streams[number] = false;
                    sound = audio;
                }
            }
        }

        Clip = new AviClip(width, height, perSecond, total, sound);
    }

    /// <summary>
    /// One stream's header and format: whether it is video, its own rate, which is more exact than the main
    /// header's, and its sound when it is PCM audio.
    /// </summary>
    private static (bool Video, double? Rate, AviSound? Sound) ReadStream(ReadOnlySpan<byte> strl)
    {
        var type = "";
        double? rate = null;
        AviSound? sound = null;
        foreach (var (code, body) in Children(strl))
        {
            if (code == "strh" && body.Length >= 28)
            {
                type = Encoding.ASCII.GetString(body, 0, 4);
                var scale = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20));
                var perScale = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24));
                rate = scale > 0 && perScale > 0 ? (double)perScale / scale : null;
            }
            else if (code == "strf" && type == "auds" && body.Length >= 16
                     && BinaryPrimitives.ReadUInt16LittleEndian(body) == 1)
            {
                sound = new AviSound(
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14)));
            }
        }

        return (type == "vids", type == "vids" ? rate : null, sound);
    }

    /// <summary>The chunks inside a list's body, each cut off at the end of the body if it claims more.</summary>
    private static List<(string Code, byte[] Body)> Children(ReadOnlySpan<byte> list)
    {
        var children = new List<(string, byte[])>();
        var at = 0;
        while (at + ChunkHeader <= list.Length)
        {
            var code = Encoding.ASCII.GetString(list.Slice(at, 4));
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(list[(at + 4)..]), list.Length - at - ChunkHeader);
            children.Add((code, list.Slice(at + ChunkHeader, length).ToArray()));
            at += ChunkHeader + (int)Padded((uint)length);
        }

        return children;
    }

    private string Code(int at) => Encoding.ASCII.GetString(Bytes(at, 4));

    private byte[] Bytes(int at, int count) => buffer.GetRange(at, count).ToArray();

    /// <summary>Lets go of <paramref name="count"/> bytes: those buffered, then as many more as arrive.</summary>
    private void Discard(long count)
    {
        var held = (int)Math.Min(count, buffer.Count);
        buffer.RemoveRange(0, held);
        skipping = count - held;
        consumed += count;
    }

    /// <summary>Chunks are stored at even lengths, a pad byte following an odd one.</summary>
    private static long Padded(long length) => length + (length & 1);
}
