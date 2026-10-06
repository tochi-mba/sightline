using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sightline.Protocol;

namespace Sightline.Testing;

/// <summary>
/// The camera's RTSP server on port 8080, needing no hardware.
/// </summary>
/// <remarks>
/// <para>
/// It behaves as the reference camera was measured to: it answers DESCRIBE with the real SDP and a
/// body length, SETUP with a session made of one byte repeated and the port it streams from, and after
/// PLAY it sends its pictures as datagrams to the port SETUP named on its <see cref="Sockets"/> — but only
/// once the control channel has started the stream, which is what <see cref="StreamStarted"/> asks. Until
/// then, and after the last picture unless told otherwise, it simply goes quiet, as the real one does.
/// </para>
/// <para>
/// The stream ends when either side closes this connection: the client by disposing it, or the camera,
/// which <see cref="HangUp"/> does as browse mode does on the real one, and which
/// <see cref="ClosesAfterFrames"/> does once its last picture has been taken.
/// </para>
/// </remarks>
public sealed partial class FakeRtspCamera : ICameraTransport
{
    /// <summary>The SDP the reference camera sent on 2026-10-02.</summary>
    public const string ReferenceSdp =
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\na=type:broadcast\r\nt=0 0\r\nc=IN IP4 0.0.0.0\r\n"
        + "m=video 0 RTP/AVP 26\r\na=control:track0\r\nm=audio 0 RTP/AVP 97\r\na=rtpmap:97 L16/16000/1\r\na=control:track1\r\n";

    /// <summary>The port the reference camera streamed from on 2026-10-06.</summary>
    public const int ReferenceServerPort = 59728;

    private readonly Queue<byte[]> outbox = new();
    private readonly TaskCompletionSource hungUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FakeCameraDatagrams? streamingTo;
    private int? clientPort;
    private ushort sequence;

    /// <summary>Where the datagrams go: the sockets of the network this connection was opened on.</summary>
    public FakeCameraSockets? Sockets { get; set; }

    /// <summary>Whether the control channel has started the media flow; no packets flow until it has.</summary>
    public Func<bool> StreamStarted { get; set; } = () => true;

    /// <summary>The pictures to send after PLAY, in order. Each goes as one marked RTP packet.</summary>
    public List<byte[]> Frames { get; } = [];

    /// <summary>Datagrams sent after PLAY before any picture: noise that is not RTP at all.</summary>
    public List<byte[]> NoiseAfterPlay { get; } = [];

    /// <summary>When set, this connection fails with it once the last picture has been taken.</summary>
    public Exception? BreaksWith { get; set; }

    /// <summary>When set, the camera closes this connection once the last picture has been taken.</summary>
    public bool ClosesAfterFrames { get; set; }

    /// <summary>The status SETUP answers with.</summary>
    public int SetupStatus { get; set; } = 200;

    /// <summary>The Transport header SETUP answers with, or null for the reference camera's.</summary>
    public string? SetupTransport { get; set; }

    /// <summary>The status PLAY answers with.</summary>
    public int PlayStatus { get; set; } = 200;

    /// <summary>A verb the camera never answers, to stand for a wedged server.</summary>
    public string? NeverAnswers { get; set; }

    /// <summary>How long each picture takes to arrive after the one before, as the real camera's dozen a second do.</summary>
    public TimeSpan Pace { get; set; }

    /// <summary>The verbs received, in order.</summary>
    public List<string> Verbs { get; } = [];

    /// <summary>The requests received, whole, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>Whether the client has closed this connection.</summary>
    public bool WasDisposed { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var request = Encoding.ASCII.GetString(bytes.Span);
        var verb = request.Split(' ')[0];
        Requests.Add(request);
        Verbs.Add(verb);
        if (verb == NeverAnswers)
        {
            return Task.CompletedTask;
        }

        switch (verb)
        {
            case "DESCRIBE":
                Reply(200, ReferenceSdp);
                break;
            case "SETUP":
                var port = ClientPort().Match(request);
                clientPort = port.Success ? int.Parse(port.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                var transport = SetupTransport
                    ?? $"RTP/AVP;unicast;client_port={clientPort}-{clientPort + 1};server_port={ReferenceServerPort}-{ReferenceServerPort + 1}";
                Reply(SetupStatus, "", SetupStatus == 200
                    ? [("Transport", transport), ("Session", "636363636363636363636363636363")]
                    : []);
                break;
            case "PLAY":
                Reply(PlayStatus, "");
                if (PlayStatus == 200 && StreamStarted() && clientPort is { } to && Sockets?.At(to) is { } socket)
                {
                    streamingTo = socket;
                    foreach (var noise in NoiseAfterPlay)
                    {
                        socket.Deliver(noise);
                    }

                    foreach (var frame in Frames)
                    {
                        socket.Deliver(Packet(frame, sequence++), Pace);
                    }
                }

                break;
            default:
                Reply(200, "", [("Public", "DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE")]);
                break;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        if (outbox.Count == 0)
        {
            if (hungUp.Task.IsCompleted)
            {
                return 0;
            }

            if ((ClosesAfterFrames || BreaksWith is not null) && Verbs.Contains("PLAY"))
            {
                // The camera ends the stream once the last picture has gone, not before.
                await (streamingTo?.Idle ?? Task.CompletedTask).WaitAsync(cancellationToken).ConfigureAwait(false);
                return BreaksWith is { } broken ? throw broken : 0;
            }

            // Quiet: nothing comes until whoever is reading gives up, or the camera hangs up.
            await hungUp.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        // At most the reader's buffer, like a real socket; the remainder waits its turn.
        var next = outbox.Dequeue();
        var take = Math.Min(next.Length, into.Length);
        next.AsSpan(0, take).CopyTo(into.Span);
        if (take < next.Length)
        {
            var rest = outbox.ToArray();
            outbox.Clear();
            outbox.Enqueue(next[take..]);
            foreach (var item in rest)
            {
                outbox.Enqueue(item);
            }
        }

        return take;
    }

    /// <summary>The camera ends the stream, as entering browse mode does: what is in flight is dropped.</summary>
    public void HangUp()
    {
        outbox.Clear();
        streamingTo?.Drop();
        hungUp.TrySetResult();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Closing the connection is what stops the real camera sending.
        IsConnected = false;
        WasDisposed = true;
        streamingTo?.Drop();
        return ValueTask.CompletedTask;
    }

    /// <summary>A block shaped like a JPEG, which is all reassembly and the session need.</summary>
    public static byte[] Jpeg(int length, byte fill = 0x5A)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }

    private void Reply(int status, string body, (string Name, string Value)[]? headers = null)
    {
        var text = new StringBuilder($"RTSP/1.0 {status} {status switch { 200 => "OK", _ => "Error" }}\r\nCSeq: 1\r\n");
        foreach (var (name, value) in headers ?? [])
        {
            text.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (body.Length > 0)
        {
            text.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        }

        outbox.Enqueue(Encoding.UTF8.GetBytes(text.Append("\r\n").Append(body).ToString()));
    }

    /// <summary>One RTP/JPEG packet carrying a whole picture: 640 by 360, the marker set, numbered <paramref name="sequence"/>.</summary>
    public static byte[] Packet(byte[] jpeg, ushort sequence)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        var packet = new byte[20 + jpeg.Length];
        packet[0] = 0x80;
        packet[1] = 0x80 | 26;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (sequence + 1u) * 7380u);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x63636363);
        packet[16] = 1;
        packet[17] = 1;
        packet[18] = 640 / 8;
        packet[19] = 360 / 8;
        jpeg.CopyTo(packet, 20);
        return packet;
    }

    [GeneratedRegex(@"client_port=(\d+)")]
    private static partial Regex ClientPort();
}
