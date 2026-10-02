using System.Net;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.Core;

/// <summary>
/// One conversation with one camera: its control channel, and its picture when asked for.
/// </summary>
/// <remarks>
/// <para>
/// The control connection is opened once and held for the life of this object, because the
/// camera's firmware treats that socket as the sign that a client is still there. Closing it
/// stops a recording and tears down the stream, so this type closes it only when disposed.
/// </para>
/// <para>
/// The two transports are supplied by a factory so tests can stand a fake camera behind both.
/// </para>
/// </remarks>
public sealed class CameraSession : IAsyncDisposable
{
    private readonly Func<int, ICameraTransport> transports;
    private readonly string host;

    private CameraSession(GpSockConnection control, Func<int, ICameraTransport> transports, string host)
    {
        Control = control;
        this.transports = transports;
        this.host = host;
    }

    /// <summary>The control channel.</summary>
    public GpSockConnection Control { get; }

    /// <summary>Opens a session against a real camera, binding to the right local address.</summary>
    /// <param name="camera">The camera's address; the family default when omitted.</param>
    /// <param name="cancellationToken">Gives up connecting.</param>
    /// <exception cref="CameraNotReachableException">This machine is not on the camera's network.</exception>
    public static Task<CameraSession> OpenAsync(IPAddress? camera = null, CancellationToken cancellationToken = default)
    {
        var address = camera ?? CameraAddress.Default;
        var local = CameraAddress.LocalAddressFor(address)
            ?? throw new CameraNotReachableException(
                $"This machine has no address on the camera's network ({address}). Join the camera's Wi-Fi first.");

        return OpenAsync(
            port => new TcpCameraTransport(new IPEndPoint(address, port), local),
            address.ToString(),
            cancellationToken);
    }

    /// <summary>Opens a session over transports from <paramref name="transports"/>.</summary>
    /// <param name="transports">Makes a transport to a given port on the camera.</param>
    /// <param name="host">The camera's address, as RTSP URLs must name it.</param>
    /// <param name="cancellationToken">Gives up connecting.</param>
    public static async Task<CameraSession> OpenAsync(
        Func<int, ICameraTransport> transports, string host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transports);
        var control = new GpSockConnection(transports(GpSockConnection.Port));
        try
        {
            await control.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new CameraSession(control, transports, host);
    }

    /// <summary>
    /// Takes one picture from the live stream, without touching the camera's card.
    /// </summary>
    /// <remarks>
    /// The stream has to be started on the control channel first; RTSP alone negotiates happily
    /// and then delivers nothing.
    /// </remarks>
    /// <param name="timeout">How long to wait for a whole picture.</param>
    /// <param name="cancellationToken">Gives up.</param>
    /// <exception cref="TimeoutException">No whole picture arrived in time.</exception>
    public async Task<CameraFrame> GrabFrameAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // Returning from inside the loop disposes the stream, which is what sends its TEARDOWN.
            await foreach (var frame in StreamFramesAsync(deadline.Token).ConfigureAwait(false))
            {
                return frame;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                          && exception is OperationCanceledException or TimeoutException)
        {
            throw new TimeoutException($"No whole picture arrived within {timeout.TotalSeconds:0} seconds.", exception);
        }

        throw new RtspException("The camera stopped sending before a whole picture arrived.");
    }

    /// <summary>
    /// The live picture, frame after frame, until cancelled or the camera stops.
    /// </summary>
    /// <remarks>
    /// The stream is started on the control channel first, for the reason
    /// <see cref="GrabFrameAsync"/> gives. Frames that are not whole JPEGs are skipped rather than
    /// passed on, so a consumer can decode everything it receives.
    /// </remarks>
    /// <param name="cancellationToken">Stops the stream.</param>
    public async IAsyncEnumerable<CameraFrame> StreamFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var rtsp = new RtspClient(transports(RtspClient.Port), host);
        try
        {
            await StartStreamAsync(rtsp, cancellationToken).ConfigureAwait(false);

            var reassembler = new RtpJpegReassembler();
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytes = await ReadOrStallAsync(rtsp, cancellationToken).ConfigureAwait(false);
                if (bytes.IsEmpty)
                {
                    yield break;
                }

                foreach (var frame in reassembler.Push(bytes.Span))
                {
                    if (RtpJpegReassembler.LooksLikeJpeg(frame.Jpeg))
                    {
                        yield return frame;
                    }
                }
            }
        }
        finally
        {
            // On every way out - cancelled, failed, or the consumer simply stopped iterating. The
            // camera's RTSP server is single-threaded and does not reap an abandoned session: one
            // left without a TEARDOWN stops it answering anybody until the camera is restarted,
            // and putting its Wi-Fi to sleep and back does not clear it. Found the hard way.
            await TryTeardownAsync(rtsp).ConfigureAwait(false);
        }
    }

    /// <summary>How long the stream may take to start before it is called stuck.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the stream may go silent before it is called stopped.</summary>
    /// <remarks>At about 12 pictures a second, this is many dozens of missing frames.</remarks>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Starts the media flow and negotiates the stream, all within <see cref="StartTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the iterator because C# will not yield inside a try with a catch, and turning a
    /// silent hang into a <see cref="TimeoutException"/> needs one. Without a deadline, a camera that
    /// never acknowledges the start leaves a live view black forever with nothing said.
    /// </remarks>
    private async Task StartStreamAsync(RtspClient rtsp, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StartTimeout);
        try
        {
            await Control.StartStreamingAsync(deadline.Token).ConfigureAwait(false);
            await rtsp.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await rtsp.DescribeAsync(deadline.Token).ConfigureAwait(false);
            var setup = await rtsp.SetupVideoAsync(deadline.Token).ConfigureAwait(false);
            if (!setup.IsSuccess)
            {
                throw new RtspException($"The camera refused the video track ({setup.StatusCode}).");
            }

            var play = await rtsp.PlayAsync(deadline.Token).ConfigureAwait(false);
            if (!play.IsSuccess)
            {
                throw new RtspException($"The camera would not start the stream ({play.StatusCode}).");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The camera did not start its stream within {StartTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadOrStallAsync(RtspClient rtsp, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await rtsp.ReadStreamAsync(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The camera sent nothing for {StallTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task TryTeardownAsync(RtspClient rtsp)
    {
        if (rtsp.Session is null)
        {
            // SETUP never succeeded, so there is no session on the camera to end.
            return;
        }

        try
        {
            using var shortly = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await rtsp.TeardownAsync(shortly.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or RtspException
                                              or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // Best effort, and it runs from a finally: throwing here would replace whatever error
            // brought the stream down with a less useful one about saying goodbye.
        }
    }

    /// <summary>
    /// Ends the session.
    /// </summary>
    /// <remarks>The camera stops recording and streaming when this happens.</remarks>
    public ValueTask DisposeAsync() => Control.DisposeAsync();
}

/// <summary>This machine cannot reach the camera, usually because it is not on its Wi-Fi.</summary>
public sealed class CameraNotReachableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CameraNotReachableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CameraNotReachableException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public CameraNotReachableException(string message, Exception inner) : base(message, inner)
    {
    }
}
