using System.Buffers.Binary;
using System.Text;

namespace Sightline.Testing;

/// <summary>
/// An AVI clip in the reference camera's shape, with every field the tests vary under their control: Motion JPEG
/// in <c>00dc</c> chunks and 16 kHz mono PCM in <c>01wb</c>, a stream header each, an index at the end.
/// </summary>
public sealed record FakeClip(byte[][] Pictures, byte[][] Sounds)
{
    public uint MicrosPerFrame { get; init; } = 33333;
    public uint Scale { get; init; } = 1;
    public uint Rate { get; init; } = 30;
    public ushort SoundFormat { get; init; } = 1;
    public bool Grouped { get; init; }
    public bool Truncated { get; init; }
    public bool Overlong { get; init; }
    public (string Code, byte[] Body)[] Extra { get; init; } = [];

    /// <summary>How many pictures come before each run of sound, as the camera writes a run after the pictures taken during it.</summary>
    public int PicturesPerSound { get; init; } = 1;

    /// <summary>A clip shaped like the reference camera's, holding these pictures and runs of sound.</summary>
    public static FakeClip Reference(byte[][] pictures, byte[][] sounds) => new(pictures, sounds);

    public byte[] Build()
    {
        var avih = new byte[Truncated ? 20 : 56];
        if (!Truncated)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(avih, MicrosPerFrame);
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), (uint)Pictures.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(32), 1920);
            BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(36), 1080);
        }

        var videoHeader = StreamHeader("vids", Scale, Rate);
        var videoFormat = new byte[40];
        var soundHeader = StreamHeader("auds", 1, 16000);
        var soundFormat = new byte[Truncated ? 10 : 18];
        if (!Truncated)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(soundFormat, SoundFormat);
            BinaryPrimitives.WriteUInt16LittleEndian(soundFormat.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(soundFormat.AsSpan(4), 16000);
            BinaryPrimitives.WriteUInt16LittleEndian(soundFormat.AsSpan(14), 16);
        }

        var hdrl = List("hdrl",
            Chunk("avih", avih),
            List("strl", Chunk("strh", Truncated ? videoHeader[..20] : videoHeader), Chunk("strf", videoFormat)),
            List("strl", Chunk("strh", soundHeader), Chunk("strf", soundFormat)),
            Overlong ? [.. Encoding.ASCII.GetBytes("JUNK"), 0xFF, 0xFF, 0, 0] : []);

        var movi = new List<byte[]>();
        var picture = 0;
        foreach (var sound in Sounds)
        {
            for (var i = 0; i < PicturesPerSound && picture < Pictures.Length; i++)
            {
                movi.Add(Chunk("00dc", Pictures[picture++]));
            }

            movi.Add(Chunk("01wb", sound));
        }

        while (picture < Pictures.Length)
        {
            movi.Add(Chunk("00dc", Pictures[picture++]));
        }

        movi.AddRange(Extra.Select(e => Chunk(e.Code, e.Body)));
        var moviList = Grouped ? List("movi", List("rec ", [.. movi])) : List("movi", [.. movi]);
        var body = new List<byte>(Encoding.ASCII.GetBytes("AVI "));
        body.AddRange(hdrl);
        body.AddRange(List("INFO", Chunk("ISFT", Encoding.ASCII.GetBytes("camera\0"))));
        body.AddRange(Chunk("JUNK", new byte[12]));
        body.AddRange(moviList);
        body.AddRange(Chunk("idx1", new byte[16 * (Pictures.Length + Sounds.Length)]));
        return [.. Encoding.ASCII.GetBytes("RIFF"), .. Le(body.Count), .. body];
    }

    private static byte[] StreamHeader(string type, uint scale, uint rate)
    {
        var header = new byte[56];
        Encoding.ASCII.GetBytes(type).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), scale);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), rate);
        return header;
    }

    private static byte[] Chunk(string code, byte[] body) =>
        [.. Encoding.ASCII.GetBytes(code), .. Le(body.Length), .. body, .. body.Length % 2 == 1 ? new byte[] { 0 } : []];

    private static byte[] List(string type, params byte[][] children)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes(type));
        foreach (var child in children)
        {
            body.AddRange(child);
        }

        return [.. Encoding.ASCII.GetBytes("LIST"), .. Le(body.Count), .. body];
    }

    private static byte[] Le(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }
}
