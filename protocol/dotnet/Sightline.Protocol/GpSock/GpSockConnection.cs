using System.Buffers.Binary;

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
/// </remarks>
public sealed class GpSockConnection : IAsyncDisposable
{
    /// <summary>The port the control server listens on.</summary>
    public const int Port = 8081;

    /// <summary>
    /// The camera's own buffer size, which is why long answers arrive in pieces.
    /// </summary>
    /// <remarks>
    /// The firmware's buffer is 256 bytes, of which the header takes 14, so a chunk carries at
    /// most 242. A reader that assumed one answer per frame would silently truncate the menu.
    /// </remarks>
    public const int MaxChunkPayload = 242;

    private readonly ICameraTransport transport;
    private readonly byte[] receiveBuffer = new byte[8192];
    private readonly List<byte> pending = [];

    /// <summary>Wraps a transport that is already connected, or about to be.</summary>
    public GpSockConnection(ICameraTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

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
        await transport.SendAsync(GpSockFrame.Encode(command, payload.Span), cancellationToken)
            .ConfigureAwait(false);
        return await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a command and gathers the chunked answer the camera streams back.
    /// </summary>
    /// <remarks>
    /// The menu, a thumbnail and a file's bytes all arrive as a run of acknowledgements of at most
    /// <see cref="MaxChunkPayload"/> bytes, ended by an empty one.
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
        await transport.SendAsync(GpSockFrame.Encode(command, payload.Span), cancellationToken)
            .ConfigureAwait(false);

        var gathered = new List<byte>();
        while (true)
        {
            var response = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (response.Type == GpSockType.Nak)
            {
                throw new GpSockRefusedException(command, response.Nak);
            }

            if (response.IsEndOfChunks)
            {
                return [.. gathered];
            }

            gathered.AddRange(response.Payload);
            onProgress?.Report(gathered.Count);
        }
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

    /// <summary>How many files are on the card.</summary>
    public async Task<int> GetFileCountAsync(CancellationToken cancellationToken = default)
    {
        var response = await DemandAsync(GpSockCommand.PlaybackGetFileCount, default, cancellationToken)
            .ConfigureAwait(false);
        return response.Payload.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(response.Payload)
            : 0;
    }

    /// <summary>Writes one setting.</summary>
    /// <param name="id">The menu id, from the camera's own catalogue.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">Gives up waiting.</param>
    public Task SetSettingAsync(int id, int value, CancellationToken cancellationToken = default)
    {
        // Layout from the firmware: a 32-bit little-endian id, a size byte, then the value.
        var payload = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(payload, id);
        payload[4] = 1;
        payload[5] = (byte)value;
        return DemandAsync(GpSockCommand.MenuSetParameter, payload, cancellationToken);
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

    /// <summary>
    /// Closes the control channel.
    /// </summary>
    /// <remarks>
    /// This is not free: the camera stops recording and tears down the stream when it happens. It
    /// belongs at the end of a session and nowhere else.
    /// </remarks>
    public ValueTask DisposeAsync() => transport.DisposeAsync();
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
        NakCode.NoStorage => "there is no memory card in it",
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
