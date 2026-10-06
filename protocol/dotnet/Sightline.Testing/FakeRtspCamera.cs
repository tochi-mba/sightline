using System.Buffers.Binary;
using System.Text;
using Sightline.Protocol;

namespace Sightline.Testing;

/// <summary>
/// The camera's RTSP server on port 8080, needing no hardware.
/// </summary>
/// <remarks>
/// <para>
/// It behaves as the reference camera was measured to: it answers DESCRIBE with the real SDP and a
/// body length, SETUP with a session made of one byte repeated, and after PLAY it sends bare RTP
/// with no interleaved framing — but only once the control channel has started the stream, which
/// is what <see cref="StreamStarted"/> asks. Until then, and after the last frame unless told to
/// close, it simply goes quiet, as the real one does.
/// </para>
/// <para>
/// Like the real one it cannot be stopped: TEARDOWN answers 501 and PAUSE answers 200, and the
/// pictures keep coming either way. Only the camera can end it, which <see cref="HangUp"/> does as
/// browse mode does on the real camera. That the camera answers just one connection per power-on is
/// the business of whoever hands these out; <see cref="NeverAnswers"/> is how a refused one looks.
/// </para>
/// </remarks>
public sealed class FakeRtspCamera : ICameraTransport
{
    /// <summary>The SDP the reference camera sent on 2026-10-02.</summary>
    public const string ReferenceSdp =
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\na=type:broadcast\r\nt=0 0\r\nc=IN IP4 0.0.0.0\r\n"
        + "m=video 0 RTP/AVP 26\r\na=control:track0\r\nm=audio 0 RTP/AVP 97\r\na=rtpmap:97 L16/16000/1\r\na=control:track1\r\n";

    private readonly Queue<byte[]> outbox = new();
    private readonly TaskCompletionSource hungUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<byte[]> paced = new(ReferenceEqualityComparer.Instance);
    private ushort sequence;

    /// <summary>Whether the control channel has started the media flow; no packets flow until it has.</summary>
    public Func<bool> StreamStarted { get; set; } = () => true;

    /// <summary>The pictures to send after PLAY, in order. Each goes as one marked RTP packet.</summary>
    public List<byte[]> Frames { get; } = [];

    /// <summary>Bytes sent after PLAY before any packet: noise that is not RTP at all.</summary>
    public byte[]? NoiseAfterPlay { get; set; }

    /// <summary>When set, reading after the last frame fails with this, as a connection that drops does.</summary>
    public Exception? BreaksWith { get; set; }

    /// <summary>When set, the connection closes after the last frame instead of going quiet.</summary>
    public bool ClosesAfterFrames { get; set; }

    /// <summary>The status SETUP answers with.</summary>
    public int SetupStatus { get; set; } = 200;

    /// <summary>The status PLAY answers with.</summary>
    public int PlayStatus { get; set; } = 200;

    /// <summary>A verb the camera never answers, to stand for a wedged server.</summary>
    public string? NeverAnswers { get; set; }

    /// <summary>How long each picture takes to arrive after the one before, as the real camera's dozen a second do.</summary>
    public TimeSpan Pace { get; set; }

    /// <summary>The verbs received, in order.</summary>
    public List<string> Verbs { get; } = [];

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var verb = Encoding.ASCII.GetString(bytes.Span).Split(' ')[0];
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
                Reply(SetupStatus, "", SetupStatus == 200 ? ("Session", "636363636363636363636363636363") : null);
                break;
            case "PLAY":
                Reply(PlayStatus, "");
                if (PlayStatus == 200 && StreamStarted())
                {
                    if (NoiseAfterPlay is { } noise)
                    {
                        outbox.Enqueue(noise);
                    }

                    foreach (var frame in Frames)
                    {
                        var packet = Packet(frame);
                        paced.Add(packet);
                        outbox.Enqueue(packet);
                    }

                    // A picture is only known to be over when the next packet starts, as on the wire.
                    outbox.Enqueue(Packet([0xFF, 0xD8, 0xFF, 0xD9]));
                }

                break;
            case "TEARDOWN":
                // Listed by OPTIONS, and not implemented: the stream carries on regardless.
                Reply(501, "");
                break;
            case "PAUSE":
                // Answered, and ignored.
                Reply(200, "");
                break;
            default:
                Reply(200, "", ("Public", "DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE"));
                break;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        if (outbox.Count == 0)
        {
            if (BreaksWith is { } broken && Verbs.Contains("PLAY"))
            {
                throw broken;
            }

            if ((ClosesAfterFrames && Verbs.Contains("PLAY")) || hungUp.Task.IsCompleted)
            {
                return 0;
            }

            // Quiet: nothing comes until whoever is reading gives up, or the camera hangs up.
            await hungUp.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (Pace > TimeSpan.Zero && paced.Remove(outbox.Peek()))
        {
            await Task.Delay(Pace, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The camera ends the connection, as entering browse mode does: what is in flight is dropped.</summary>
    public void HangUp()
    {
        outbox.Clear();
        hungUp.TrySetResult();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private void Reply(int status, string body, (string Name, string Value)? header = null)
    {
        var text = new StringBuilder($"RTSP/1.0 {status} {status switch { 200 => "OK", 501 => "Not Implemented", _ => "Error" }}\r\nCSeq: 1\r\n");
        if (header is { } h)
        {
            text.Append(h.Name).Append(": ").Append(h.Value).Append("\r\n");
        }

        if (body.Length > 0)
        {
            text.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        }

        outbox.Enqueue(Encoding.UTF8.GetBytes(text.Append("\r\n").Append(body).ToString()));
    }

    /// <summary>One RTP/JPEG packet carrying a whole picture: 640 by 360, the marker set.</summary>
    private byte[] Packet(byte[] jpeg)
    {
        var packet = new byte[20 + jpeg.Length];
        packet[0] = 0x80;
        packet[1] = 0x80 | 26;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sequence * 7380u);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x63636363);
        packet[16] = 1;
        packet[17] = 1;
        packet[18] = 640 / 8;
        packet[19] = 360 / 8;
        jpeg.CopyTo(packet, 20);
        return packet;
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
}
