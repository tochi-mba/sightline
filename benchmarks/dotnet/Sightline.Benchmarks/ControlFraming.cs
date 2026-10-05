using BenchmarkDotNet.Attributes;
using Sightline.Benchmarks.Camera;
using Sightline.Protocol.GpSock;

namespace Sightline.Benchmarks;

/// <summary>
/// Building and reading GPSOCKET frames, which every command and every answer passes through.
/// </summary>
/// <remarks>
/// A status poll is one request and one answer; the settings menu arrives in pieces of at most 242
/// bytes; a gigabyte of video is about 17,500 frames of 60 KB. What one frame costs decides how much
/// of a download is Sightline rather than the Wi-Fi.
/// </remarks>
public class ControlFraming
{
    // A field rather than a constant, so the compiler cannot fold the request into its answer.
    private readonly GpSockCommand status = GpSockCommand.GetDeviceStatus;

    private byte[] statusAnswer = [];
    private byte[] menuChunk = [];
    private byte[] downloadFrame = [];
    private byte[] refusal = [];

    [GlobalSetup]
    public void Setup()
    {
        statusAnswer = Answers.Ack(GpSockCommand.GetDeviceStatus, ReferenceCamera.Status());
        menuChunk = Answers.Ack(
            GpSockCommand.GetParameterFile, ReferenceCamera.Menu().AsSpan(0, GpSockConnection.MaxChunkPayload));
        downloadFrame = Answers.Ack(GpSockCommand.PlaybackGetRawData, new byte[Answers.DownloadFrameBytes]);
        refusal = Answers.Refusal(GpSockCommand.PlaybackGetFileCount, NakCode.ServerBusy);

        Expect.Equal(GpSockFrame.RequestHeaderLength, EncodeStatusRequest().Length, "the status request's length");
        Expect.Equal(16, DecodeStatusAnswer().Payload.Length, "the status answer's payload");
        Expect.Equal(GpSockConnection.MaxChunkPayload, DecodeMenuChunk().Payload.Length, "a menu chunk's payload");
        Expect.Equal(Answers.DownloadFrameBytes, DecodeDownloadFrame().Payload.Length, "a download frame's payload");
        Expect.Equal(NakCode.ServerBusy, DecodeRefusal().Nak, "the refusal's reason");
    }

    /// <summary>The request every status poll sends.</summary>
    [Benchmark]
    public byte[] EncodeStatusRequest() => GpSockFrame.Encode(status);

    /// <summary>The reference camera's 16-byte status answer.</summary>
    [Benchmark]
    public GpSockResponse DecodeStatusAnswer() => Decode(statusAnswer);

    /// <summary>One full piece of the settings menu.</summary>
    [Benchmark]
    public GpSockResponse DecodeMenuChunk() => Decode(menuChunk);

    /// <summary>One full frame of a file being downloaded.</summary>
    [Benchmark]
    public GpSockResponse DecodeDownloadFrame() => Decode(downloadFrame);

    /// <summary>The camera saying it is busy, which is how it refuses most things.</summary>
    [Benchmark]
    public GpSockResponse DecodeRefusal() => Decode(refusal);

    private static GpSockResponse Decode(byte[] wire)
    {
        _ = GpSockFrame.TryDecode(wire, out var response, out _);
        return response;
    }
}
