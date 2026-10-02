using Sightline.Protocol.GpSock;

namespace Sightline.Protocol.Tests;

/// <summary>
/// A camera that needs no hardware, speaking the real wire format.
/// </summary>
/// <remarks>
/// It answers the way the reference camera did, including the behaviours that are easy to get
/// wrong: long answers arrive in chunks ended by an empty one, browsing is refused unless the
/// camera was put in browse mode first, and the frames can be delivered in awkward pieces so the
/// reader is exercised the way TCP really exercises it.
/// </remarks>
public sealed class FakeCamera : ICameraTransport
{
    private readonly Queue<byte[]> outbox = new();
    private readonly List<byte> inbox = [];

    /// <summary>How many bytes at a time this camera will hand over; 0 means all of them.</summary>
    public int DribbleBytes { get; set; }

    /// <summary>
    /// When set, the camera accepts commands and never answers, as one that has gone does.
    /// </summary>
    /// <remarks>
    /// This is what the access point going to sleep mid-session looks like to the client: the
    /// socket is still there, the write succeeds, and nothing ever comes back.
    /// </remarks>
    public bool HangsUp { get; set; }

    /// <summary>Which mode the camera is in. Browsing is refused in any other.</summary>
    public CameraMode Mode { get; private set; } = CameraMode.Record;

    /// <summary>Whether the camera is recording to its card.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>Whether the media flow has been started.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>How many photographs have been taken.</summary>
    public int PicturesTaken { get; private set; }

    /// <summary>What the menu command returns.</summary>
    public string MenuXml { get; set; } =
        """
        <Menu version="1.0"><Categories><Category><Name>Record</Name><Settings>
          <Setting><Name>Resolution</Name><ID>0x0000000</ID><Type>0x00</Type><Default>0x00</Default>
            <Values><Value><ID>0x00</ID><Name>4K</Name></Value>
                    <Value><ID>0x01</ID><Name>1080P</Name></Value></Values></Setting>
        </Settings></Category></Categories></Menu>
        """;

    /// <summary>The settings this camera has been told to change.</summary>
    public List<(int Id, int Value)> SettingsWritten { get; } = [];

    /// <summary>How many files it claims are on the card.</summary>
    public int FileCount { get; set; } = 2;

    /// <summary>The status payload it reports; 16 bytes, as the reference camera sends.</summary>
    public byte[] Status { get; set; } =
        Convert.FromHexString("000280010025b3000000fe7c0000a501");

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>Whether anything closed the connection, which a real camera reacts badly to.</summary>
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
        inbox.AddRange(bytes.Span);
        while (inbox.Count >= GpSockFrame.RequestHeaderLength)
        {
            var frame = inbox.ToArray();
            if (!frame.AsSpan(0, 8).SequenceEqual("GPSOCKET"u8))
            {
                // The real firmware answers nothing at all, which is the whole reason the port
                // looks dead to a scanner.
                inbox.Clear();
                return Task.CompletedTask;
            }

            var command = (GpSockCommand)((frame[10] << 8) | frame[11]);
            var payload = frame.AsSpan(GpSockFrame.RequestHeaderLength).ToArray();
            inbox.Clear();
            if (!HangsUp)
            {
                Handle(command, payload);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        if (outbox.Count == 0)
        {
            return Task.FromResult(0);
        }

        var next = outbox.Peek();
        var take = DribbleBytes > 0 ? Math.Min(DribbleBytes, next.Length) : next.Length;
        take = Math.Min(take, into.Length);
        next.AsSpan(0, take).CopyTo(into.Span);
        if (take == next.Length)
        {
            outbox.Dequeue();
        }
        else
        {
            outbox.Dequeue();
            outbox.Enqueue(next[take..]);
            // Keep order: the remainder must come before anything queued after it.
            for (var i = 0; i < outbox.Count - 1; i++)
            {
                outbox.Enqueue(outbox.Dequeue());
            }
        }

        return Task.FromResult(take);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        IsConnected = false;
        // What the real camera does when the control socket goes: it stops.
        IsRecording = false;
        IsStreaming = false;
        return ValueTask.CompletedTask;
    }

    private void Handle(GpSockCommand command, byte[] payload)
    {
        switch (command)
        {
            case GpSockCommand.GetDeviceStatus:
                var status = (byte[])Status.Clone();
                status[0] = (byte)Mode;
                status[1] = (byte)((IsRecording ? 1 : 0) | 0b10);
                Ack(command, status);
                break;

            case GpSockCommand.GetParameterFile:
                Chunked(command, System.Text.Encoding.UTF8.GetBytes(MenuXml));
                break;

            case GpSockCommand.SetMode:
                Mode = (CameraMode)payload[0];
                Ack(command, []);
                break;

            case GpSockCommand.RestartStreaming:
                IsStreaming = true;
                Ack(command, []);
                break;

            case GpSockCommand.CapturePicture:
                PicturesTaken++;
                Ack(command, []);
                break;

            case GpSockCommand.RecordToggle:
                IsRecording = !IsRecording;
                Ack(command, []);
                break;

            case GpSockCommand.PlaybackGetFileCount:
                if (Mode != CameraMode.Browse)
                {
                    Nak(command, NakCode.ServerBusy);
                    break;
                }

                Ack(command, [(byte)(FileCount & 0xFF), (byte)(FileCount >> 8)]);
                break;

            case GpSockCommand.MenuSetParameter:
                SettingsWritten.Add((BitConverter.ToInt32(payload), payload[5]));
                Ack(command, []);
                break;

            default:
                Nak(command, NakCode.InvalidCommand);
                break;
        }
    }

    private void Ack(GpSockCommand command, byte[] payload) =>
        outbox.Enqueue(Frame(GpSockType.Ack, command, payload));

    private void Nak(GpSockCommand command, NakCode reason)
    {
        // As the firmware sends it: the reason in the size slot, and no payload.
        var frame = Frame(GpSockType.Nak, command, []);
        BitConverter.GetBytes((short)reason).CopyTo(frame, 12);
        outbox.Enqueue(frame);
    }

    private void Chunked(GpSockCommand command, byte[] whole)
    {
        for (var offset = 0; offset < whole.Length; offset += GpSockConnection.MaxChunkPayload)
        {
            var size = Math.Min(GpSockConnection.MaxChunkPayload, whole.Length - offset);
            Ack(command, whole.AsSpan(offset, size).ToArray());
        }

        Ack(command, []);
    }

    private static byte[] Frame(GpSockType type, GpSockCommand command, byte[] payload)
    {
        var frame = new byte[GpSockFrame.ResponseHeaderLength + payload.Length];
        "GPSOCKET"u8.CopyTo(frame);
        frame[8] = (byte)((ushort)type & 0xFF);
        frame[9] = (byte)((ushort)type >> 8);
        frame[10] = (byte)((ushort)command >> 8);
        frame[11] = (byte)((ushort)command & 0xFF);
        frame[12] = (byte)(payload.Length & 0xFF);
        frame[13] = (byte)(payload.Length >> 8);
        payload.CopyTo(frame, GpSockFrame.ResponseHeaderLength);
        return frame;
    }
}
