using System.Text;
using Sightline.Protocol;

namespace Sightline.Benchmarks.Camera;

/// <summary>
/// The camera's RTSP port: the handshake answered at once, then a stream replayed a read at a time.
/// </summary>
/// <remarks>
/// <para>
/// The replies are the reference camera's in shape, as docs/PROTOCOL.md records them: an SDP with a
/// JPEG video track and a 16 kHz PCM audio track, a session id that is one byte repeated, and after
/// PLAY bare RTP with no interleaved framing. When the stream runs out the read returns nothing,
/// which is how a session sees the camera stop, and the TEARDOWN that follows is answered too, so
/// every session ends the way a real one does rather than through an exception.
/// </para>
/// <para>
/// Like <see cref="ScriptedCamera"/> it decides nothing per request and allocates nothing per read.
/// </para>
/// </remarks>
internal sealed class ReplayedStream : ICameraTransport
{
    private const string Session = "DEDEDEDEDEDEDEDEDEDEDEDEDEDEDE";

    private const string Sdp =
        "v=0\r\n" +
        "o=- 0 0 IN IP4 0.0.0.0\r\n" +
        "s=stream\r\n" +
        "t=0 0\r\n" +
        "m=video 0 RTP/AVP 26\r\n" +
        "a=control:track0\r\n" +
        "m=audio 0 RTP/AVP 97\r\n" +
        "a=rtpmap:97 L16/16000/1\r\n" +
        "a=control:track1\r\n";

    private static readonly byte[] DescribeReply = Reply(
        "CSeq: 1\r\nContent-Type: application/sdp\r\n" +
        $"Content-Length: {Sdp.Length}\r\n\r\n{Sdp}");

    private static readonly byte[] SetupReply = Reply(
        $"CSeq: 2\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\nSession: {Session};timeout=60\r\n\r\n");

    private static readonly byte[] PlayReply = Reply($"CSeq: 3\r\nSession: {Session}\r\nRange: npt=0.000-\r\n\r\n");

    private static readonly byte[] TeardownReply = Reply($"CSeq: 4\r\nSession: {Session}\r\n\r\n");

    private readonly byte[] stream;
    private readonly int readSize;
    private byte[] reply = [];
    private int replySent;
    private int streamSent;

    /// <summary>Replays <paramref name="stream"/> after PLAY, at most <paramref name="readSize"/> bytes a read.</summary>
    public ReplayedStream(byte[] stream, int readSize)
    {
        this.stream = stream;
        this.readSize = readSize;
    }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>Starts again from the first byte of the stream, for the next session.</summary>
    public ReplayedStream Rewound()
    {
        reply = [];
        replySent = 0;
        streamSent = 0;
        return this;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        // A session sends DESCRIBE, SETUP, PLAY and TEARDOWN, and their first letters differ.
        reply = bytes.Span[0] switch
        {
            (byte)'D' => DescribeReply,
            (byte)'S' => SetupReply,
            (byte)'P' => PlayReply,
            _ => TeardownReply,
        };
        replySent = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        if (replySent < reply.Length)
        {
            var count = Math.Min(into.Length, reply.Length - replySent);
            reply.AsSpan(replySent, count).CopyTo(into.Span);
            replySent += count;
            return CompletedReads.Of(count);
        }

        var take = Math.Min(Math.Min(into.Length, readSize), stream.Length - streamSent);
        stream.AsSpan(streamSent, take).CopyTo(into.Span);
        streamSent += take;
        return CompletedReads.Of(take);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private static byte[] Reply(string afterStatus) => Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\n" + afterStatus);
}
