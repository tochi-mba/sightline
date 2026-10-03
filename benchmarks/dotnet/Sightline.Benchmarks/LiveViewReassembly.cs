using BenchmarkDotNet.Attributes;
using Sightline.Benchmarks.Camera;
using Sightline.Protocol.Rtp;

namespace Sightline.Benchmarks;

/// <summary>
/// Turning the camera's packets back into pictures, which every frame of the live view pays for.
/// </summary>
/// <remarks>
/// <para>
/// At 12.2 frames a second the camera leaves about 82 ms for each frame, and reassembly, the session
/// around it, decoding and drawing all have to fit inside that. Whatever reassembly allocates per
/// frame the garbage collector clears up while the picture is moving, which is a stutter on a slow
/// phone and battery on any phone.
/// </para>
/// <para>
/// One operation is one frame. Ten seconds of the synthetic stream go through one long-lived
/// reassembler, as in a real session, cut into reads of a fixed size: 1,460 bytes is one TCP
/// segment, the smallest read a socket usually hands over, and 8 KB and 64 KB stand for reads that
/// find data already queued.
/// </para>
/// </remarks>
public class LiveViewReassembly
{
    private const int Frames = 122;

    private readonly RtpJpegReassembler reassembler = new();
    private byte[] stream = [];

    /// <summary>How many bytes each read hands over.</summary>
    [Params(1460, 8192, 65536)]
    public int ReadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        stream = SyntheticVideo.Stream(SyntheticVideo.Pictures(SyntheticVideo.DistinctPictures), Frames);

        // A picture is only known to be finished when the next one starts, so the first pass leaves
        // its last picture waiting and every later pass finishes the one before it.
        Expect.Equal(Frames - 1, FrameFromPackets(), "pictures from the first ten seconds");
        Expect.Equal(Frames, FrameFromPackets(), "pictures from every later ten seconds");
    }

    /// <summary>One picture, reassembled from the packets that carried it.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public int FrameFromPackets()
    {
        var pictures = 0;
        for (var offset = 0; offset < stream.Length; offset += ReadSize)
        {
            pictures += reassembler.Push(stream.AsSpan(offset, Math.Min(ReadSize, stream.Length - offset))).Count;
        }

        return pictures;
    }
}
