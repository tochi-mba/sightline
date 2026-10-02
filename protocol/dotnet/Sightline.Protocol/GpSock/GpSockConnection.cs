using System.Buffers.Binary;
using System.Text;

namespace Sightline.Protocol.GpSock;

/// <summary>
/// The camera's control channel: one connection, held open for the whole session.
/// </summary>
/// <remarks>
/// <para>
/// <b>This connection must not be opened per command.</b> The camera's firmware treats the socket
/// as the client's liveness signal: when it closes, the camera stops recording, tears down the
/// stream and aborts a burst capture. A client that connected, sent a shutter command and
/// disconnected would therefore take the photograph and immediately stop whatever else the camera
/// was doing. Holding it open is also what keeps the camera's access point from going to sleep.
/// </para>
/// <para>
/// The camera is single-client, so a second connection — from this app or from a vendor app — is
/// refused or starves. That is the "camera busy" case the UI explains.
/// </para>
/// <para>
/// Every answer names the command it answers; the firmware echoes it. A frame that answers some
/// other command is a leftover from a request that was abandoned — a cancelled download, say —
/// and is skipped rather than handed to whoever asked next.
/// </para>
/// </remarks>
public sealed class GpSockConnection : IAsyncDisposable
{
    /// <summary>The port the control server listens on.</summary>
    public const int Port = 8081;

    /// <summary>
    /// The camera's own buffer size, which is why long answers arrive in pieces.
    /// </summary>
    /// <remarks>
    /// The firmware's buffer is 256 bytes, of which the header takes 14, so a chunk of the menu
    /// carries at most 242. A reader that assumed one answer per frame would silently truncate it.
    /// A file's bytes come in much larger frames, up to 60 KB each.
    /// </remarks>
    public const int MaxChunkPayload = 242;

    /// <summary>How long a cancelled transfer may take to wind down before the channel is given up on.</summary>
    public static readonly TimeSpan WindDownTimeout = TimeSpan.FromSeconds(5);

    private readonly ICameraTransport transport;
    private readonly byte[] receiveBuffer = new byte[64 * 1024];
    private readonly List<byte> pending = [];

    /// <summary>
    /// One request in flight at a time.
    /// </summary>
    /// <remarks>
    /// An app sends a shutter press while the live view is still starting the stream, and both go
    /// down this one socket. Without this, the two requests interleave and each reads the other's
    /// answer — the camera did both things and the app reports the wrong outcome for each.
    /// </remarks>
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    private string? outOfStep;

    /// <summary>Wraps a transport that is already connected, or about to be.</summary>
    public GpSockConnection(ICameraTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>How many leftover answers to abandoned requests have been skipped.</summary>
    public int StaleFramesSkipped { get; private set; }

    /// <summary>Opens the control channel.</summary>
    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        transport.ConnectAsync(cancellationToken);

    /// <summary>Sends a command and waits for the camera's answer.</summary>
    /// <param name="command">What to ask for.</param>
    /// <param name="payload">Its argument, empty for most commands.</param>
    /// <param name="cancellationToken">Gives up waiting.</param>
    /// <returns>The camera's answer, which may be a refusal.</returns>
    public async Task<GpSockResponse> AskAsync(
        GpSockCommand command,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default)
    {
        await oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfOutOfStep();
            await transport.SendAsync(GpSockFrame.Encode(command, payload.Span), cancellationToken)
                .ConfigureAwait(false);
            return await ReadAnswerAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    /// <summary>
    /// Sends a command and gathers the chunked answer the camera streams back.
    /// </summary>
    /// <remarks>
    /// The menu and a thumbnail arrive as a run of acknowledgements ended by an empty one. For a
    /// file's bytes use <see cref="DownloadAsync"/>, which does not hold the whole file in memory.
    /// </remarks>
    /// <param name="command">What to ask for.</param>
    /// <param name="payload">Its argument.</param>
    /// <param name="onProgress">Called with the running total, for a progress bar.</param>
    /// <param name="cancellationToken">Gives up waiting.</param>
    /// <exception cref="GpSockRefusedException">The camera refused part-way through.</exception>
    public async Task<byte[]> AskForChunksAsync(
        GpSockCommand command,
        ReadOnlyMemory<byte> payload = default,
        IProgress<int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var gathered = new MemoryStream();
        await StreamChunksAsync(
            command,
            payload,
            async (chunk, token) =>
            {
                await gathered.WriteAsync(chunk, token).ConfigureAwait(false);
                onProgress?.Report((int)gathered.Length);
            },
            cancellationToken).ConfigureAwait(false);
        return gathered.ToArray();
    }

    /// <summary>Sends a command and throws unless the camera accepted it.</summary>
    /// <exception cref="GpSockRefusedException">The camera refused.</exception>
    public async Task<GpSockResponse> DemandAsync(
        GpSockCommand command,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default)
    {
        var response = await AskAsync(command, payload, cancellationToken).ConfigureAwait(false);
        return response.IsAck ? response : throw new GpSockRefusedException(command, response.Nak);
    }

    /// <summary>Reads the camera's state.</summary>
    public async Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await DemandAsync(GpSockCommand.GetDeviceStatus, default, cancellationToken)
            .ConfigureAwait(false);
        return new DeviceStatus(response.Payload);
    }

    /// <summary>Reads the camera's own settings catalogue.</summary>
    public async Task<MenuCatalog> GetMenuAsync(CancellationToken cancellationToken = default)
    {
        var xml = await AskForChunksAsync(
            GpSockCommand.GetParameterFile, default, null, cancellationToken).ConfigureAwait(false);
        return MenuCatalog.Parse(xml);
    }

    /// <summary>Switches the camera between recording, taking photographs and browsing the card.</summary>
    public Task SetModeAsync(CameraMode mode, CancellationToken cancellationToken = default) =>
        DemandAsync(GpSockCommand.SetMode, new[] { (byte)mode }, cancellationToken);

    /// <summary>
    /// Starts the media flow.
    /// </summary>
    /// <remarks>
    /// Must be sent for the stream to carry anything. RTSP SETUP and PLAY can both answer 200 and
    /// still deliver nothing until this has gone.
    /// </remarks>
    public Task StartStreamingAsync(CancellationToken cancellationToken = default) =>
        DemandAsync(GpSockCommand.RestartStreaming, default, cancellationToken);

    /// <summary>Takes a photograph.</summary>
    public Task CapturePictureAsync(CancellationToken cancellationToken = default) =>
        DemandAsync(GpSockCommand.CapturePicture, default, cancellationToken);

    /// <summary>Starts or stops recording to the camera's card.</summary>
    public Task ToggleRecordingAsync(CancellationToken cancellationToken = default) =>
        DemandAsync(GpSockCommand.RecordToggle, default, cancellationToken);

    /// <summary>Turns the camera off. The session ends with it.</summary>
    public Task PowerOffAsync(CancellationToken cancellationToken = default) =>
        DemandAsync(GpSockCommand.PowerOff, default, cancellationToken);

    /// <summary>
    /// How many files are on the card.
    /// </summary>
    /// <remarks>
    /// Needs <see cref="CameraMode.Browse"/>. The firmware answers an empty card with
    /// <see cref="NakCode.NoStorage"/>, the same refusal as no card at all, so this returns 0 for
    /// both and the caller says so in words that cover either.
    /// </remarks>
    public async Task<int> GetFileCountAsync(CancellationToken cancellationToken = default)
    {
        var response = await AskAsync(GpSockCommand.PlaybackGetFileCount, default, cancellationToken)
            .ConfigureAwait(false);
        if (response.Type == GpSockType.Nak)
        {
            return response.Nak == NakCode.NoStorage
                ? 0
                : throw new GpSockRefusedException(GpSockCommand.PlaybackGetFileCount, response.Nak);
        }

        return response.Payload.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(response.Payload)
            : 0;
    }

    /// <summary>
    /// Every file on the card, newest page last, as the camera lists them.
    /// </summary>
    /// <remarks>
    /// Needs <see cref="CameraMode.Browse"/>. The list comes in pages: the first is asked for with
    /// a flag, and each later one by the index of the last file already seen. Paging stops at the
    /// count the camera gave, at an empty page, or at a page that adds nothing new — a camera that
    /// kept repeating itself must not keep an app here forever.
    /// </remarks>
    public async Task<IReadOnlyList<CameraFile>> GetFileListAsync(CancellationToken cancellationToken = default)
    {
        var count = await GetFileCountAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<CameraFile>(count);
        var seen = new HashSet<int>();
        var first = true;
        var lastIndex = 0;
        while (files.Count < count)
        {
            var request = new byte[] { (byte)(first ? 1 : 0), (byte)(lastIndex & 0xFF), (byte)(lastIndex >> 8) };
            var response = await DemandAsync(GpSockCommand.PlaybackGetFileList, request, cancellationToken)
                .ConfigureAwait(false);
            var added = 0;
            foreach (var file in CameraFile.ParsePage(response.Payload))
            {
                if (files.Count < count && seen.Add(file.Index))
                {
                    files.Add(file);
                    lastIndex = file.Index;
                    added++;
                }
            }

            if (added == 0)
            {
                break;
            }

            first = false;
        }

        return files;
    }

    /// <summary>A file's thumbnail, as a JPEG. Needs <see cref="CameraMode.Browse"/> with the stream stopped.</summary>
    public Task<byte[]> GetThumbnailAsync(int index, CancellationToken cancellationToken = default) =>
        AskForChunksAsync(GpSockCommand.PlaybackGetThumbnail, Index(index), null, cancellationToken);

    /// <summary>
    /// Copies a file off the card into <paramref name="destination"/>, a frame at a time.
    /// </summary>
    /// <remarks>
    /// Needs <see cref="CameraMode.Browse"/>. A 4K video is gigabytes, so nothing is gathered in
    /// memory. Cancelling is safe: the camera stops sending as soon as it sees another request, so
    /// a cancelled download sends one, lets the camera finish what was already in flight, and
    /// leaves the channel ready for the next command.
    /// </remarks>
    /// <param name="index">The file's index, from <see cref="GetFileListAsync"/>.</param>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="progress">Called with the running total of bytes.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <returns>How many bytes were written.</returns>
    public async Task<long> DownloadAsync(
        int index,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        long total = 0;
        await StreamChunksAsync(
            GpSockCommand.PlaybackGetRawData,
            Index(index),
            async (chunk, token) =>
            {
                await destination.WriteAsync(chunk, token).ConfigureAwait(false);
                total += chunk.Length;
                progress?.Report(total);
            },
            cancellationToken).ConfigureAwait(false);
        return total;
    }

    /// <summary>Deletes a file from the card. Needs <see cref="CameraMode.Browse"/>.</summary>
    public Task DeleteFileAsync(int index, CancellationToken cancellationToken = default) =>
        DemandAsync(GpSockCommand.PlaybackDeleteFile, Index(index), cancellationToken);

    /// <summary>
    /// Reads one setting's current value, as the bytes the camera sent.
    /// </summary>
    /// <remarks>
    /// A choice comes back as one byte, its value id; text comes back as its characters. The menu
    /// says which a setting is. An id the firmware does not know is answered with a zero rather
    /// than a refusal, so the menu, not this, is what says a setting exists.
    /// </remarks>
    public async Task<byte[]> GetSettingAsync(int id, CancellationToken cancellationToken = default)
    {
        var request = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(request, id);
        var response = await DemandAsync(GpSockCommand.MenuGetParameter, request, cancellationToken)
            .ConfigureAwait(false);
        return response.Payload;
    }

    /// <summary>Reads a choice setting's current value id.</summary>
    /// <exception cref="GpSockProtocolException">The camera sent nothing for it.</exception>
    public async Task<int> GetChoiceAsync(int id, CancellationToken cancellationToken = default)
    {
        var value = await GetSettingAsync(id, cancellationToken).ConfigureAwait(false);
        return value.Length >= 1
            ? value[0]
            : throw new GpSockProtocolException($"The camera sent no value for setting 0x{id:X4}.");
    }

    /// <summary>Reads a text setting, such as the camera's Wi-Fi name.</summary>
    public async Task<string> GetTextAsync(int id, CancellationToken cancellationToken = default)
    {
        var value = await GetSettingAsync(id, cancellationToken).ConfigureAwait(false);
        var end = Array.IndexOf(value, (byte)0);
        return Encoding.ASCII.GetString(value, 0, end < 0 ? value.Length : end).Trim();
    }

    /// <summary>Writes a choice setting.</summary>
    /// <param name="id">The menu id, from the camera's own catalogue.</param>
    /// <param name="value">The value id to select.</param>
    /// <param name="cancellationToken">Gives up waiting.</param>
    public Task SetSettingAsync(int id, int value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, byte.MaxValue);
        // Layout from the firmware: a 32-bit little-endian id, a size byte, then the value.
        return DemandAsync(GpSockCommand.MenuSetParameter, SetPayload(id, [(byte)value]), cancellationToken);
    }

    /// <summary>
    /// Writes a text setting into the camera's fixed-size field.
    /// </summary>
    /// <remarks>
    /// The firmware copies exactly <paramref name="fieldLength"/> bytes, whatever was sent, so the
    /// text is padded with zeros to that length; sending less would let it copy whatever happened to
    /// follow in its buffer. The length is the camera's, learned by reading the field first.
    /// </remarks>
    /// <param name="id">The menu id.</param>
    /// <param name="value">Printable ASCII, no longer than the field.</param>
    /// <param name="fieldLength">The size of the camera's field for this setting.</param>
    /// <param name="cancellationToken">Gives up waiting.</param>
    /// <exception cref="ArgumentException">The text is empty, too long, or not printable ASCII.</exception>
    public Task SetTextAsync(int id, string value, int fieldLength, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(fieldLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fieldLength, byte.MaxValue);
        if (value.Length == 0 || value.Length > fieldLength || value.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException(
                $"The camera takes 1 to {fieldLength} printable characters here.", nameof(value));
        }

        var field = new byte[fieldLength];
        Encoding.ASCII.GetBytes(value, field);
        return DemandAsync(GpSockCommand.MenuSetParameter, SetPayload(id, field), cancellationToken);
    }

    /// <summary>
    /// Sends a command whose answer is a run of chunks, handing each to <paramref name="onChunk"/>
    /// as it arrives.
    /// </summary>
    private async Task StreamChunksAsync(
        GpSockCommand command,
        ReadOnlyMemory<byte> payload,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken)
    {
        await oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfOutOfStep();
            await transport.SendAsync(GpSockFrame.Encode(command, payload.Span), cancellationToken)
                .ConfigureAwait(false);

            // Until the camera's last word on this command has been read, it may still be sending.
            // Leaving early for any reason — cancelled, or the destination failing to take a chunk
            // — must stop it first, or its remaining frames become the answer to the next request.
            var finished = false;
            try
            {
                while (!finished)
                {
                    var response = await ReadAnswerAsync(command, cancellationToken).ConfigureAwait(false);
                    if (response.Type == GpSockType.Nak)
                    {
                        finished = true;
                        throw new GpSockRefusedException(command, response.Nak);
                    }

                    finished = response.IsEndOfChunks;
                    if (!finished)
                    {
                        await onChunk(response.Payload, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (!finished)
                {
                    await WindDownAsync(command).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    /// <summary>
    /// Stops a transfer the camera is still sending, and reads past what it already sent.
    /// </summary>
    /// <remarks>
    /// The firmware checks for a new request between frames and abandons the transfer when it sees
    /// one, answering the transfer with a refusal and then the new request as usual. So this sends
    /// the cheapest request there is, reads to the transfer's last word, then reads that request's
    /// own answer, leaving nothing behind for the next command to mistake for its reply. If the
    /// camera does not wind down in time the channel is marked out of step, and every later request
    /// fails at once rather than reading part of a file as its answer.
    /// </remarks>
    private async Task WindDownAsync(GpSockCommand transfer)
    {
        using var deadline = new CancellationTokenSource(WindDownTimeout);
        try
        {
            await transport.SendAsync(GpSockFrame.Encode(GpSockCommand.GetDeviceStatus), deadline.Token)
                .ConfigureAwait(false);
            GpSockResponse response;
            do
            {
                response = await ReadAnswerAsync(transfer, deadline.Token).ConfigureAwait(false);
            }
            while (response.Type != GpSockType.Nak && !response.IsEndOfChunks);

            await ReadAnswerAsync(GpSockCommand.GetDeviceStatus, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
                                              or GpSockProtocolException or InvalidOperationException
                                              or System.Net.Sockets.SocketException)
        {
            outOfStep = $"A cancelled {transfer} did not wind down ({exception.Message}).";
        }
    }

    private void ThrowIfOutOfStep()
    {
        if (outOfStep is not null)
        {
            throw new GpSockProtocolException($"The control channel is out of step and must be reopened. {outOfStep}");
        }
    }

    private async Task<GpSockResponse> ReadAnswerAsync(GpSockCommand expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (response.Command == expected)
            {
                return response;
            }

            StaleFramesSkipped++;
        }
    }

    private async Task<GpSockResponse> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (GpSockFrame.TryDecode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending),
                    out var response, out var consumed))
            {
                pending.RemoveRange(0, consumed);
                return response;
            }

            var read = await transport.ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new GpSockProtocolException("The camera closed the control channel.");
            }

            pending.AddRange(receiveBuffer.AsSpan(0, read));
        }
    }

    private static byte[] Index(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, ushort.MaxValue);
        return [(byte)(index & 0xFF), (byte)(index >> 8)];
    }

    private static byte[] SetPayload(int id, ReadOnlySpan<byte> value)
    {
        var payload = new byte[5 + value.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, id);
        payload[4] = (byte)value.Length;
        value.CopyTo(payload.AsSpan(5));
        return payload;
    }

    /// <summary>
    /// Closes the control channel.
    /// </summary>
    /// <remarks>
    /// This is not free: the camera stops recording and tears down the stream when it happens. It
    /// belongs at the end of a session and nowhere else.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await transport.DisposeAsync().ConfigureAwait(false);
        oneAtATime.Dispose();
    }
}

/// <summary>The camera refused a command, and said why.</summary>
public sealed class GpSockRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GpSockRefusedException(GpSockCommand command, NakCode reason)
        : base($"The camera refused {command}: {Explain(reason)}.")
    {
        Command = command;
        Reason = reason;
    }

    /// <summary>Creates the exception.</summary>
    public GpSockRefusedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public GpSockRefusedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public GpSockRefusedException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>What was asked for.</summary>
    public GpSockCommand Command { get; }

    /// <summary>Why the camera would not.</summary>
    public NakCode Reason { get; }

    /// <summary>The refusal in words a person can act on.</summary>
    public static string Explain(NakCode reason) => reason switch
    {
        NakCode.Ok => "no error",
        NakCode.ServerBusy => "it is busy, which usually means it is in the wrong mode or still streaming",
        NakCode.InvalidCommand => "it does not know that command",
        NakCode.RequestTimeout => "it gave up waiting",
        NakCode.ModeError => "that cannot be done in the mode it is in",
        NakCode.NoStorage => "there is no memory card in it, or the card is empty",
        NakCode.WriteFail => "the card could not be written",
        NakCode.GetFileListFail => "it could not read the file list",
        NakCode.GetThumbnailFail => "it could not read the thumbnail",
        NakCode.FullStorage => "the card is full",
        NakCode.BatteryLow => "its battery is too low",
        NakCode.MemoryError => "it ran out of memory",
        NakCode.ChecksumError => "a checksum did not match",
        NakCode.SyncTimeError => "it would not take the clock",
        _ => $"reason {(int)reason}",
    };
}
