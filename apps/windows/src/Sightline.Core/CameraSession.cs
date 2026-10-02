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

        await Control.StartStreamingAsync(deadline.Token).ConfigureAwait(false);

        await using var rtsp = new RtspClient(transports(RtspClient.Port), host);
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

        var reassembler = new RtpJpegReassembler();
        try
        {
            while (true)
            {
                var bytes = await rtsp.ReadStreamAsync(deadline.Token).ConfigureAwait(false);
                if (bytes.IsEmpty)
                {
                    throw new RtspException("The camera stopped sending before a whole picture arrived.");
                }

                foreach (var frame in reassembler.Push(bytes.Span))
                {
                    if (RtpJpegReassembler.LooksLikeJpeg(frame.Jpeg))
                    {
                        await TryTeardownAsync(rtsp).ConfigureAwait(false);
                        return frame;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No whole picture arrived within {timeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task TryTeardownAsync(RtspClient rtsp)
    {
        try
        {
            using var shortly = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await rtsp.TeardownAsync(shortly.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or RtspException
                                              or System.Net.Sockets.SocketException)
        {
            // The picture is already in hand; a camera that does not answer the goodbye is not a
            // reason to throw it away.
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
