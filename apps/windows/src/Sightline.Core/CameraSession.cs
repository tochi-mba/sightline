using System.Globalization;
using System.Net;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.Core;

/// <summary>
/// One conversation with one camera: its control channel, and its stream while anybody watches it.
/// </summary>
/// <remarks>
/// <para>
/// The control connection is opened once and held for the life of this object, because the
/// camera's firmware treats that socket as the sign that a client is still there. Closing it
/// stops a recording and tears down the stream, so this type closes it only when disposed.
/// </para>
/// <para>
/// The connections and the stream's socket come from an <see cref="ICameraSockets"/>, so tests can stand
/// a fake camera behind them all.
/// </para>
/// </remarks>
public sealed class CameraSession : IAsyncDisposable
{
    // What is sent from the stream's port to the camera's, so a firewall lets the stream in as the reply.
    private static readonly byte[] Opener = [0];

    private readonly ICameraSockets sockets;
    private readonly string host;
    private readonly CameraSessionTiming timing;
    private readonly Lock feedGate = new();
    private readonly List<Task> retiring = [];
    private LiveFeed? feed;
    private volatile bool closed;

    private CameraSession(GpSockConnection control, ICameraSockets sockets, string host, CameraSessionTiming timing)
    {
        Control = control;
        this.sockets = sockets;
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
    /// which step went quiet is the difference between a fix and a guess.
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
    public static Task<CameraSession> OpenAsync(IPAddress camera, IPAddress local, CancellationToken cancellationToken = default) =>
        OpenAsync(new BoundCameraSockets(camera, local), camera.ToString(), timing: null, cancellationToken);

    /// <summary>Opens a session over <paramref name="sockets"/>.</summary>
    /// <param name="sockets">Connections to the camera, and sockets for its stream.</param>
    /// <param name="host">The camera's address, as RTSP URLs must name it.</param>
    /// <param name="timing">How long the stream may take to start or stay silent; the real values when omitted.</param>
    /// <param name="cancellationToken">Gives up connecting.</param>
    public static async Task<CameraSession> OpenAsync(
        ICameraSockets sockets, string host, CameraSessionTiming? timing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sockets);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var control = new GpSockConnection(sockets.Transport(GpSockConnection.Port));
        try
        {
            await control.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new CameraSession(control, sockets, host, timing ?? CameraSessionTiming.Default);
    }

    /// <summary>
    /// Takes one picture from the stream, without touching the camera's card.
    /// </summary>
    /// <remarks>
    /// The picture comes from the stream everybody else is watching, which this starts if nobody is, and
    /// which stops again afterwards if nobody else wants it.
    /// </remarks>
    /// <param name="timeout">How long to wait for a whole picture.</param>
    /// <param name="cancellationToken">Gives up.</param>
    /// <exception cref="TimeoutException">No whole picture arrived in time.</exception>
    /// <exception cref="RtspException">The camera refused the stream, or ended it before a picture arrived.</exception>
    public async Task<CameraFrame> GrabFrameAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var pictures = Subscribe();
        try
        {
            return await pictures.NextAsync(deadline.Token).ConfigureAwait(false) ?? throw EndedBeforeAPicture();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                          && exception is OperationCanceledException or TimeoutException)
        {
            throw new TimeoutException($"No whole picture arrived within {timeout.TotalSeconds:0} seconds.", exception);
        }
    }

    /// <summary>
    /// The camera's pictures, one after another, until the camera ends its stream or this is cancelled,
    /// which both end it quietly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everybody watching shares one stream, started on the control channel first: RTSP alone negotiates
    /// happily and then delivers nothing. The stream stops when its last watcher leaves, and the camera
    /// ends it on its own when its card is browsed; asking again starts another.
    /// </para>
    /// <para>
    /// Pictures that are not whole JPEGs are skipped, so a consumer can decode everything it receives.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops these pictures.</param>
    /// <exception cref="TimeoutException">The stream did not start in time, or the camera went quiet.</exception>
    /// <exception cref="RtspException">The stream could not start, or its connection or socket failed.</exception>
    public async IAsyncEnumerable<CameraFrame> StreamFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var pictures = Subscribe();
        while (await NextOrNothingAsync(pictures, cancellationToken).ConfigureAwait(false) is { } frame)
        {
            yield return frame;
        }
    }

    /// <summary>
    /// Ends the session: the stream, then the control channel.
    /// </summary>
    /// <remarks>The camera stops recording when the control channel goes. Anybody still watching sees the pictures end.</remarks>
    public async ValueTask DisposeAsync()
    {
        LiveFeed? current;
        lock (feedGate)
        {
            closed = true;
            current = feed;
            feed = null;
        }

        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }

        Task[] tidying;
        lock (feedGate)
        {
            tidying = [.. retiring];
        }

        await Task.WhenAll(tidying).ConfigureAwait(false);
        await Control.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Why a stream that ended quietly gave no picture: the session closing, or the camera ending it.</summary>
    private Exception EndedBeforeAPicture() => closed
        ? new ObjectDisposedException(nameof(CameraSession), "The session closed before a whole picture arrived.")
        : new RtspException("The camera ended its stream before a whole picture arrived.");

    /// <summary>The next picture, or nothing once the stream is over or the watcher has stopped watching.</summary>
    /// <remarks>Apart from the iterator because C# will not yield inside a try with a catch.</remarks>
    private static async Task<CameraFrame?> NextOrNothingAsync(LiveFeed.Subscription pictures, CancellationToken cancellationToken)
    {
        try
        {
            return await pictures.NextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Joins the stream that is running, or starts one if none is.</summary>
    private LiveFeed.Subscription Subscribe()
    {
        lock (feedGate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (feed?.TrySubscribe() is { } joined)
            {
                return joined;
            }

            // None is running, or the one there ended a moment ago and is on its way out.
            feed = new LiveFeed(StartStreamAsync, timing.Stall, () => Trace, Retire, out var first);
            return first;
        }
    }

    /// <summary>
    /// Starts the media flow and negotiates the stream, all within <see cref="CameraSessionTiming.Start"/>.
    /// </summary>
    /// <returns>The stream's RTSP connection, played, and the socket its pictures arrive at.</returns>
    /// <exception cref="TimeoutException">It did not start in time.</exception>
    private async Task<(RtspClient Rtsp, ICameraDatagrams Datagrams)> StartStreamAsync(CancellationToken stop)
    {
        RtspClient? rtsp = null;
        ICameraDatagrams? datagrams = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(timing.Start);
        try
        {
            rtsp = new RtspClient(sockets.Transport(RtspClient.Port), host);
            datagrams = sockets.Datagrams();
            Trace?.Invoke("control: RestartStreaming ...");
            await Control.StartStreamingAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke("control: RestartStreaming acknowledged");

            Trace?.Invoke($"rtsp: connecting to {host}:{RtspClient.Port} ...");
            await rtsp.ConnectAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke("rtsp: connected; DESCRIBE ...");
            var describe = await rtsp.DescribeAsync(deadline.Token).ConfigureAwait(false);
            Trace?.Invoke($"rtsp: DESCRIBE {describe.StatusCode}, {describe.Body.Length} bytes of SDP");

            var setup = await rtsp.SetupVideoAsync(datagrams.Port, deadline.Token).ConfigureAwait(false);
            var from = rtsp.ServerPort?.ToString(CultureInfo.InvariantCulture) ?? "a port it did not say";
            Trace?.Invoke($"rtsp: SETUP {setup.StatusCode}, session {rtsp.Session ?? "(none)"}, from {from} to {datagrams.Port}");
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

            if (rtsp.ServerPort is { } port)
            {
                // A firewall that drops what it did not ask for lets the stream in as the reply to this.
                await datagrams.SendAsync(Opener, port, deadline.Token).ConfigureAwait(false);
            }

            return (rtsp, datagrams);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            if (rtsp is not null)
            {
                await rtsp.DisposeAsync().ConfigureAwait(false);
            }

            if (datagrams is not null)
            {
                await datagrams.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is OperationCanceledException && !stop.IsCancellationRequested)
            {
                throw new TimeoutException($"The camera did not start its stream within {timing.Start.TotalSeconds:0} seconds.", failure);
            }

            throw;
        }
    }

    /// <summary>Lets go of a stream that is over, or that nobody is watching any more.</summary>
    private void Retire(LiveFeed ended)
    {
        lock (feedGate)
        {
            if (feed == ended)
            {
                feed = null;
            }

            retiring.RemoveAll(task => task.IsCompleted);
            retiring.Add(ended.DisposeAsync().AsTask());
        }
    }
}

/// <summary>How long each step of the stream is given.</summary>
/// <param name="Start">To start: RestartStreaming, then RTSP's DESCRIBE, SETUP and PLAY.</param>
/// <param name="Stall">To go silent before the stream is called lost. At about 12 pictures a second this is
/// many dozens of missing frames.</param>
public sealed record CameraSessionTiming(TimeSpan Start, TimeSpan Stall)
{
    /// <summary>The real timings.</summary>
    public static CameraSessionTiming Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8));
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
