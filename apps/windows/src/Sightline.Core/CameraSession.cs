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
    private readonly CameraSessionTiming timing;

    private CameraSession(GpSockConnection control, Func<int, ICameraTransport> transports, string host, CameraSessionTiming timing)
    {
        Control = control;
        this.transports = transports;
        this.host = host;
        this.timing = timing;
    }

    /// <summary>The control channel.</summary>
    public GpSockConnection Control { get; }

    /// <summary>
    /// Receives one line per step of starting and running the stream, when set.
    /// </summary>
    /// <remarks>
    /// The camera gives no error when a stream fails to start; it simply sends nothing. Knowing
    /// which step went quiet is the difference between a fix and a guess, and a failed attempt can
    /// leave the camera needing a restart, so each one has to say as much as it can.
    /// </remarks>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Opens a session against a real camera, sending from <paramref name="local"/>: the address the
    /// chosen adapter was given on the camera's network.
    /// </summary>
    /// <remarks>
    /// Binding to the adapter that was actually chosen, rather than to any address on the camera's
    /// subnet, matters when two adapters have been on the camera: Windows keeps a disconnected
    /// adapter's old address on record, and traffic sent from it goes nowhere.
    /// </remarks>
    /// <param name="camera">The camera's address.</param>
    /// <param name="local">This PC's address on the camera's network.</param>
    /// <param name="cancellationToken">Gives up connecting.</param>
    public static Task<CameraSession> OpenAsync(IPAddress camera, IPAddress local, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(local);
        return OpenAsync(
            port => new TcpCameraTransport(new IPEndPoint(camera, port), local),
            camera.ToString(),
            timing: null,
            cancellationToken);
    }

    /// <summary>Opens a session over transports from <paramref name="transports"/>.</summary>
    /// <param name="transports">Makes a transport to a given port on the camera.</param>
    /// <param name="host">The camera's address, as RTSP URLs must name it.</param>
    /// <param name="timing">How long the stream may take to start or stay silent; the real values when omitted.</param>
    /// <param name="cancellationToken">Gives up connecting.</param>
    public static async Task<CameraSession> OpenAsync(
        Func<int, ICameraTransport> transports, string host, CameraSessionTiming? timing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
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

        return new CameraSession(control, transports, host, timing ?? CameraSessionTiming.Default);
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
            var received = 0L;
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytes = await ReadOrStallAsync(rtsp, cancellationToken).ConfigureAwait(false);
                if (bytes.IsEmpty)
                {
                    Trace?.Invoke($"rtsp: the camera closed the stream after {received} bytes");
                    yield break;
                }

                if (received == 0)
                {
                    Trace?.Invoke($"rtsp: first stream bytes arrived: {Convert.ToHexString(bytes.Span[..Math.Min(16, bytes.Length)])}");
                }

                received += bytes.Length;
                foreach (var frame in reassembler.Push(bytes.Span))
                {
                    if (RtpJpegReassembler.LooksLikeJpeg(frame.Jpeg))
                    {
                        yield return frame;
                    }
                    else
                    {
                        Trace?.Invoke($"rtsp: a {frame.Jpeg.Length}-byte frame was not a whole JPEG and was skipped");
                    }
                }

                if (Trace is not null && reassembler.PacketsRead == 0 && received > 64 * 1024)
                {
                    Trace($"rtsp: {received} bytes arrived but none parsed as an RTP packet");
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

    /// <summary>
    /// Starts the media flow and negotiates the stream, all within <see cref="CameraSessionTiming.Start"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the iterator because C# will not yield inside a try with a catch, and turning a
    /// silent hang into a <see cref="TimeoutException"/> needs one. Without a deadline, a camera that
    /// never acknowledges the start leaves a live view black forever with nothing said.
    /// </remarks>
    private async Task StartStreamAsync(RtspClient rtsp, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timing.Start);
        try
        {
            Trace?.Invoke("control: RestartStreaming ...");
            await Control.StartStreamingAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke("control: RestartStreaming acknowledged");

            Trace?.Invoke($"rtsp: connecting to {host}:{RtspClient.Port} ...");
            await rtsp.ConnectAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke("rtsp: connected; DESCRIBE ...");
            var describe = await rtsp.DescribeAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke($"rtsp: DESCRIBE {describe.StatusCode}, {describe.Body.Length} bytes of SDP");

            var setup = await rtsp.SetupVideoAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke($"rtsp: SETUP {setup.StatusCode}, session {rtsp.Session ?? "(none)"}");
            if (!setup.IsSuccess)
            {
                throw new RtspException($"The camera refused the video track ({setup.StatusCode}).");
            }

            var play = await rtsp.PlayAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke($"rtsp: PLAY {play.StatusCode}");
            if (!play.IsSuccess)
            {
                throw new RtspException($"The camera would not start the stream ({play.StatusCode}).");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The camera did not start its stream within {timing.Start.TotalSeconds:0} seconds.");
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadOrStallAsync(RtspClient rtsp, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(timing.Stall);
        try
        {
            return await rtsp.ReadStreamAsync(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The camera sent nothing for {timing.Stall.TotalSeconds:0} seconds.");
        }
    }

    private async Task TryTeardownAsync(RtspClient rtsp)
    {
        if (rtsp.Session is null)
        {
            // SETUP never succeeded, so there is no session on the camera to end.
            return;
        }

        try
        {
            using var shortly = new CancellationTokenSource(timing.Teardown);
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

/// <summary>How long each step of the stream is given.</summary>
/// <param name="Start">To start: RestartStreaming, then RTSP's DESCRIBE, SETUP and PLAY.</param>
/// <param name="Stall">To go silent before it is called stopped. At about 12 pictures a second this is
/// many dozens of missing frames.</param>
/// <param name="Teardown">To end the RTSP session properly on the way out.</param>
public sealed record CameraSessionTiming(TimeSpan Start, TimeSpan Stall, TimeSpan Teardown)
{
    /// <summary>The real timings.</summary>
    public static CameraSessionTiming Default { get; } =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2));
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
