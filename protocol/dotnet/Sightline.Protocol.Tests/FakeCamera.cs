using System.Buffers.Binary;
using Sightline.Protocol.GpSock;

namespace Sightline.Protocol.Tests;

/// <summary>
/// A camera that needs no hardware, speaking the real wire format.
/// </summary>
/// <remarks>
/// It answers the way the reference camera and its firmware source do, including the behaviours
/// that are easy to get wrong: long answers arrive in chunks ended by an empty one; browsing is
/// refused unless the camera was put in browse mode first, and thumbnails also while it streams;
/// an empty card is refused as "no storage"; a file's bytes are produced only as fast as they are
/// read, and any new request abandons the transfer with a refusal, as the firmware does between
/// frames; text settings live in fixed-size fields. Frames can be delivered in awkward pieces so the
/// reader is exercised the way TCP really exercises it.
/// </remarks>
public sealed class FakeCamera : ICameraTransport
{
    private readonly Queue<byte[]> outbox = new();
    private readonly List<byte> inbox = [];
    private readonly List<FakeFile> files = [];
    private (byte[] Bytes, int Offset)? download;

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
    public bool IsStreaming { get; set; }

    /// <summary>Whether it has been told to turn off.</summary>
    public bool IsPoweredOff { get; private set; }

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

    /// <summary>The choice settings this camera has been told to change, in order.</summary>
    public List<(int Id, int Value)> SettingsWritten { get; } = [];

    /// <summary>Every setting write exactly as it arrived: the id and the bytes after the size byte.</summary>
    public List<(int Id, byte[] Value)> RawSettingsWritten { get; } = [];

    /// <summary>What each setting reads back as. An id with no entry reads as a single zero, as the firmware does.</summary>
    public Dictionary<int, byte[]> Values { get; } = new()
    {
        [MenuIds.WifiName] = "ActionCam_000000000000"u8.ToArray(),
        [MenuIds.WifiPassword] = PaddedField("12345678", WifiFieldLength),
    };

    /// <summary>The size of the camera's Wi-Fi name and password fields.</summary>
    public const int WifiFieldLength = 32;

    /// <summary>How many files to put on one page of the file list.</summary>
    public int PageSize { get; set; } = 4;

    /// <summary>When set, every page of the file list is the first page, as a broken camera might send.</summary>
    public bool RepeatsFirstPage { get; set; }

    /// <summary>How many bytes of a file go in each frame of a download.</summary>
    public int DownloadChunk { get; set; } = 1000;

    /// <summary>When false, deleting is refused the way a firmware built without it refuses.</summary>
    public bool SupportsDelete { get; set; } = true;

    /// <summary>When set, a download is answered with this refusal instead of the file.</summary>
    public NakCode? RefuseDownloadWith { get; set; }

    /// <summary>Frames to send before the answer to the next command, as leftovers from earlier requests.</summary>
    public List<byte[]> StrayFramesBeforeNextAnswer { get; } = [];

    /// <summary>Acknowledgements to send instead of the usual answer, for firmware that answers oddly.</summary>
    public Dictionary<GpSockCommand, byte[]> ForcedAnswers { get; } = [];

    /// <summary>The status payload it reports; 16 bytes, as the reference camera sends.</summary>
    public byte[] Status { get; set; } =
        Convert.FromHexString("000280010025b3000000fe7c0000a501");

    /// <summary>The files on the card.</summary>
    public IReadOnlyList<FakeFile> Files => files;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>Whether anything closed the connection, which a real camera reacts badly to.</summary>
    public bool WasDisposed { get; private set; }

    /// <summary>Puts a file on the card, numbered after the last one.</summary>
    public FakeFile AddFile(char code, DateTime taken, byte[] content)
    {
        var file = new FakeFile(code, files.Count == 0 ? 1 : files[^1].Index + 1, taken, content);
        files.Add(file);
        return file;
    }

    /// <summary>A text value as the firmware stores it: zero-padded to its field.</summary>
    public static byte[] PaddedField(string text, int length)
    {
        var field = new byte[length];
        System.Text.Encoding.ASCII.GetBytes(text, field);
        return field;
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
            if (HangsUp)
            {
                continue;
            }

            if (download is not null)
            {
                // The firmware looks for a new request between frames and gives the transfer up.
                download = null;
                Nak(GpSockCommand.PlaybackGetRawData, NakCode.InvalidCommand);
            }

            foreach (var stray in StrayFramesBeforeNextAnswer)
            {
                outbox.Enqueue(stray);
            }

            StrayFramesBeforeNextAnswer.Clear();
            if (ForcedAnswers.TryGetValue(command, out var forced))
            {
                Ack(command, forced);
                continue;
            }

            Handle(command, payload);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        if (outbox.Count == 0 && download is { } transfer)
        {
            // The next frame of a file is made only when the last one has been taken, which is
            // what gives a cancel something to interrupt.
            var size = Math.Min(DownloadChunk, transfer.Bytes.Length - transfer.Offset);
            if (size == 0)
            {
                download = null;
                Ack(GpSockCommand.PlaybackGetRawData, []);
            }
            else
            {
                Ack(GpSockCommand.PlaybackGetRawData, transfer.Bytes.AsSpan(transfer.Offset, size).ToArray());
                download = (transfer.Bytes, transfer.Offset + size);
            }
        }

        if (outbox.Count == 0)
        {
            return Task.FromResult(0);
        }

        var next = outbox.Peek();
        var take = DribbleBytes > 0 ? Math.Min(DribbleBytes, next.Length) : next.Length;
        take = Math.Min(take, into.Length);
        next.AsSpan(0, take).CopyTo(into.Span);
        outbox.Dequeue();
        if (take < next.Length)
        {
            // Keep order: the remainder must come before anything queued after it.
            outbox.Enqueue(next[take..]);
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
        download = null;
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

            case GpSockCommand.PowerOff:
                IsPoweredOff = true;
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

            case GpSockCommand.PlaybackGetFileCount when Mode != CameraMode.Browse:
            case GpSockCommand.PlaybackGetFileList when Mode != CameraMode.Browse:
            case GpSockCommand.PlaybackGetRawData when Mode != CameraMode.Browse:
            case GpSockCommand.PlaybackDeleteFile when Mode != CameraMode.Browse:
            case GpSockCommand.PlaybackGetThumbnail when Mode != CameraMode.Browse || IsStreaming:
                Nak(command, NakCode.ServerBusy);
                break;

            case GpSockCommand.PlaybackGetFileCount:
                if (files.Count == 0)
                {
                    // The firmware's answer to an empty card is the same as to no card.
                    Nak(command, NakCode.NoStorage);
                    break;
                }

                Ack(command, [(byte)(files.Count & 0xFF), (byte)(files.Count >> 8)]);
                break;

            case GpSockCommand.PlaybackGetFileList:
                Ack(command, Page(payload));
                break;

            case GpSockCommand.PlaybackGetThumbnail:
                if (Find(payload) is not { } pictured)
                {
                    Nak(command, NakCode.InvalidCommand);
                    break;
                }

                Ack(command, [0xFF, 0xD8, (byte)pictured.Index, 0xFF, 0xD9]);
                Ack(command, []);
                break;

            case GpSockCommand.PlaybackGetRawData:
                if (RefuseDownloadWith is { } refusal)
                {
                    Nak(command, refusal);
                    break;
                }

                if (Find(payload) is not { } wanted)
                {
                    Nak(command, NakCode.InvalidCommand);
                    break;
                }

                download = (wanted.Content, 0);
                break;

            case GpSockCommand.PlaybackDeleteFile:
                if (!SupportsDelete || Find(payload) is not { } doomed)
                {
                    // A firmware built without delete answers 0xFFFF, which reads as "busy".
                    Nak(command, NakCode.ServerBusy);
                    break;
                }

                files.Remove(doomed);
                Ack(command, []);
                break;

            case GpSockCommand.MenuGetParameter:
                Ack(command, Values.TryGetValue(BinaryPrimitives.ReadInt32LittleEndian(payload), out var value) ? value : [0]);
                break;

            case GpSockCommand.MenuSetParameter:
                var id = BinaryPrimitives.ReadInt32LittleEndian(payload);
                var written = payload.AsSpan(5, payload[4]).ToArray();
                RawSettingsWritten.Add((id, written));
                if (written.Length == 1)
                {
                    SettingsWritten.Add((id, written[0]));
                }

                Values[id] = written;
                Ack(command, []);
                break;

            default:
                Nak(command, NakCode.InvalidCommand);
                break;
        }
    }

    private byte[] Page(byte[] payload)
    {
        var after = payload[0] == 1 || RepeatsFirstPage ? 0 : payload[1] | (payload[2] << 8);
        var page = files.Where(f => f.Index > after).Take(PageSize).ToList();
        var bytes = new byte[1 + (page.Count * CameraFile.MinimumEntryLength)];
        bytes[0] = (byte)page.Count;
        for (var i = 0; i < page.Count; i++)
        {
            var file = page[i];
            var entry = bytes.AsSpan(1 + (i * CameraFile.MinimumEntryLength));
            entry[0] = (byte)file.Code;
            BinaryPrimitives.WriteUInt16LittleEndian(entry[1..], (ushort)file.Index);
            entry[3] = (byte)(file.Taken.Year - 2000);
            entry[4] = (byte)file.Taken.Month;
            entry[5] = (byte)file.Taken.Day;
            entry[6] = (byte)file.Taken.Hour;
            entry[7] = (byte)file.Taken.Minute;
            entry[8] = (byte)file.Taken.Second;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[9..], (uint)((file.Content.Length + 1023) / 1024));
        }

        return bytes;
    }

    private FakeFile? Find(byte[] payload)
    {
        var index = payload[0] | (payload[1] << 8);
        return files.FirstOrDefault(f => f.Index == index);
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

    /// <summary>A whole response frame, for tests that need to send one out of turn.</summary>
    public static byte[] Frame(GpSockType type, GpSockCommand command, byte[] payload)
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

/// <summary>A file on the fake camera's card.</summary>
public sealed record FakeFile(char Code, int Index, DateTime Taken, byte[] Content);
