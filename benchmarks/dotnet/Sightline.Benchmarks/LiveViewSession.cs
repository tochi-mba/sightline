using BenchmarkDotNet.Attributes;
using Sightline.Benchmarks.Camera;
using Sightline.Core;
using Sightline.Protocol.GpSock;

namespace Sightline.Benchmarks;

/// <summary>
/// The live view as the apps receive it: a session starts the stream and hands over whole JPEGs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CameraSession.StreamFramesAsync"/> end to end, over a camera that answers the
/// handshake at once and replays the synthetic stream: starting the stream on the control channel,
/// RTSP, reassembly, the stall timer around every read and the check on every picture. Everything
/// short of decoding.
/// </para>
/// <para>
/// <see cref="StreamFrame"/> is one frame of ten seconds of video, starting and ending the stream
/// included, so its operations a second are the frame rate Sightline's side of the live view could
/// sustain: a ceiling to set beside the camera's 12.2, not a prediction of what a person sees.
/// <see cref="FirstPicture"/> is Sightline's own share of the wait between asking for the picture
/// and the first whole frame. The camera's share is measured only on the device, in
/// docs/ACCEPTANCE.md.
/// </para>
/// </remarks>
public class LiveViewSession
{
    private const int Frames = 122;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private CameraSession session = null!;

    /// <summary>How many bytes each read of the stream hands over; the client's buffer holds 16 KB.</summary>
    [Params(1460, 16384)]
    public int ReadSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var control = new ScriptedCamera();
        control.Answer(GpSockCommand.RestartStreaming, Answers.Ack(GpSockCommand.RestartStreaming, []));

        // One picture more than is counted: the last is only known to be finished when the next
        // one starts, and a live camera is always sending the next one.
        var pictures = SyntheticVideo.Pictures(SyntheticVideo.DistinctPictures);
        var camera = new ReplayedStream(SyntheticVideo.Stream(pictures, Frames + 1), ReadSize);
        session = await CameraSession.OpenAsync(
            port => port == GpSockConnection.Port ? control : camera.Rewound(), "camera");

        Expect.Equal(Frames, await StreamFrame(), "pictures from ten seconds of stream");
        Expect.Equal(pictures[0].Length, await FirstPicture(), "the size of the first picture");
    }

    [GlobalCleanup]
    public Task Cleanup() => session.DisposeAsync().AsTask();

    /// <summary>One frame of the live view, from bytes on the socket to a JPEG ready to decode.</summary>
    [Benchmark(OperationsPerInvoke = Frames)]
    public async Task<int> StreamFrame()
    {
        var pictures = 0;
        await foreach (var _ in session.StreamFramesAsync())
        {
            pictures++;
        }

        return pictures;
    }

    /// <summary>Starting the stream and taking its first whole picture, then ending it.</summary>
    [Benchmark]
    public async Task<int> FirstPicture() => (await session.GrabFrameAsync(Patience)).Jpeg.Length;
}
