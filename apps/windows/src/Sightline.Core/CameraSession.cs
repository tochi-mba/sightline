using System.Net;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.Core;

/// <summary>
/// One conversation with one camera: its control channel, and its one live stream once asked for.
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
    private readonly Lock feedGate = new();
    private readonly CancellationTokenSource closing = new();
    private Task<LiveFeed>? feed;
    private string? spent;

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
    /// Whether this session holds the camera's live picture: its one stream connection is open and
    /// still running, so anything that ends it, such as browsing the card, costs the live picture until
    /// the camera is switched off and on.
    /// </summary>
    public bool HoldsLivePicture
    {
        get
        {
            lock (feedGate)
            {
                return feed is not null && spent is null;
            }
        }
    }

    /// <summary>
    /// Takes one picture from the live stream, without touching the camera's card.
    /// </summary>
    /// <remarks>
    /// The picture comes from the session's one stream (see <see cref="StreamFramesAsync"/>), which this
    /// starts if nothing has yet, and leaves running.
    /// </remarks>
    /// <param name="timeout">How long to wait for a whole picture.</param>
    /// <param name="cancellationToken">Gives up.</param>
    /// <exception cref="TimeoutException">No whole picture arrived in time.</exception>
    /// <exception cref="LivePictureUnavailableException">The camera will not give this session a live picture.</exception>
    public async Task<CameraFrame> GrabFrameAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var live = await FeedAsync(deadline.Token).ConfigureAwait(false);
            using var pictures = live.Subscribe();
            return await pictures.NextAsync(timeout, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                          && exception is OperationCanceledException or TimeoutException)
        {
            throw new TimeoutException($"No whole picture arrived within {timeout.TotalSeconds:0} seconds.", exception);
        }
    }

    /// <summary>
    /// The live picture, frame after frame, until cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The camera answers one stream connection each time it is switched on, and cannot be asked to stop
    /// one: TEARDOWN is not implemented and PAUSE is ignored. So the session opens the stream once, the
    /// first time a picture is wanted, and keeps it until the session ends. Ending this enumeration stops
    /// these pictures and leaves the stream running for whoever asks next.
    /// </para>
    /// <para>
    /// The stream is started on the control channel first: RTSP alone negotiates happily and then
    /// delivers nothing. Frames that are not whole JPEGs are skipped, so a consumer can decode
    /// everything it receives.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops these pictures.</param>
    /// <exception cref="TimeoutException">
    /// Nothing arrived for <see cref="CameraSessionTiming.Stall"/>. The stream stays open; asking again waits on it again.
    /// </exception>
    /// <exception cref="LivePictureUnavailableException">
    /// The camera will not give this session a live picture: it ended the one it gave, or never started it.
    /// </exception>
    public async IAsyncEnumerable<CameraFrame> StreamFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var live = await FeedAsync(cancellationToken).ConfigureAwait(false);
        using var pictures = live.Subscribe();
        while (true)
        {
            yield return await pictures.NextAsync(timing.Stall, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends the session: the stream, then the control channel.
    /// </summary>
    /// <remarks>The camera stops recording when the control channel goes.</remarks>
    public async ValueTask DisposeAsync()
    {
        await closing.CancelAsync().ConfigureAwait(false);
        Task<LiveFeed>? started;
        lock (feedGate)
        {
            started = feed;
        }

        if (started is not null)
        {
            try
            {
                await (await started.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (LivePictureUnavailableException)
            {
                // It never started, and closed what it had opened on the way out.
            }
        }

        await Control.DisposeAsync().ConfigureAwait(false);
        closing.Dispose();
    }

    /// <summary>The session's one stream, started by whoever asks first.</summary>
    private Task<LiveFeed> FeedAsync(CancellationToken cancellationToken)
    {
        Task<LiveFeed> starting;
        lock (feedGate)
        {
            if (spent is { } reason)
            {
                throw new LivePictureUnavailableException(reason);
            }

            starting = feed ??= StartFeedAsync();
        }

        return starting.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Starts the media flow and negotiates the stream, all within <see cref="CameraSessionTiming.Start"/>.
    /// </summary>
    /// <remarks>
    /// Not tied to whoever asked first: giving up waiting must not abandon a start others may be waiting
    /// on. Any failure is final for this session, because a second connection would not be answered.
    /// </remarks>
    private async Task<LiveFeed> StartFeedAsync()
    {
        var rtsp = new RtspClient(transports(RtspClient.Port), host);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
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
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            await rtsp.DisposeAsync().ConfigureAwait(false);
            var reason = failure switch
            {
                OperationCanceledException when closing.IsCancellationRequested =>
                    "The session ended before the live picture started.",
                OperationCanceledException =>
                    $"The camera did not start its live picture within {timing.Start.TotalSeconds:0} seconds, "
                    + "which is what it does once it has given its live picture to an earlier connection.",
                _ => failure.Message,
            };
            throw new LivePictureUnavailableException(Spend(reason), failure);
        }

        return new LiveFeed(rtsp, () => Trace, Spend);
    }

    /// <summary>Records that this session's live picture is gone, and returns what to tell people.</summary>
    private string Spend(string reason)
    {
        var told = $"{reason} {LivePictureUnavailableException.Advice}";
        lock (feedGate)
        {
            spent ??= told;
        }

        return told;
    }
}

/// <summary>How long each step of the stream is given.</summary>
/// <param name="Start">To start: RestartStreaming, then RTSP's DESCRIBE, SETUP and PLAY.</param>
/// <param name="Stall">To go silent before a watcher is told so. At about 12 pictures a second this is
/// many dozens of missing frames.</param>
public sealed record CameraSessionTiming(TimeSpan Start, TimeSpan Stall)
{
    /// <summary>The real timings.</summary>
    public static CameraSessionTiming Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8));
}

/// <summary>
/// The camera will not give this session a live picture, and asking again will not change that until
/// the camera is switched off and on.
/// </summary>
/// <remarks>
/// The reference camera answers one stream connection per power-on, and ends it when its card is
/// browsed; see PROTOCOL.md, "One stream per power-on".
/// </remarks>
public sealed class LivePictureUnavailableException : Exception
{
    /// <summary>What to do about it, said after every reason.</summary>
    public const string Advice =
        "This camera gives one live picture each time it is switched on: switch it off and on to see it again.";

    /// <summary>Creates the exception.</summary>
    public LivePictureUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LivePictureUnavailableException()
        : base(Advice)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LivePictureUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
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
