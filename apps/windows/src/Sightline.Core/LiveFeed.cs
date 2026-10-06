using System.Threading.Channels;
using Sightline.Protocol;
using Sightline.Protocol.Rtp;

namespace Sightline.Core;

/// <summary>
/// One run of the camera's stream, from starting it to its end, shared by everybody who wants its pictures.
/// </summary>
/// <remarks>
/// <para>
/// It runs while anybody watches. When the last watcher leaves, even before the stream has started, it
/// ends, and closing its RTSP connection is what stops the camera sending. It also ends when the camera
/// closes that connection, as browsing its card does, or sends nothing for the stall time, or the start
/// fails; whoever asks next starts another.
/// </para>
/// <para>
/// Each watcher gets the newest picture: one that has not been taken by the time the next arrives is
/// replaced by it, so a slow watcher sees fewer pictures, never older ones. A new watcher starts with the
/// latest picture when it is under a second old, so coming back to the picture shows one at once.
/// </para>
/// </remarks>
internal sealed class LiveFeed : IAsyncDisposable
{
    private const long FreshMilliseconds = 1000;

    // Bigger than any datagram can be, so none is ever cut short.
    private const int DatagramBuffer = 64 * 1024;

    private readonly Func<CancellationToken, Task<(RtspClient Rtsp, ICameraDatagrams Datagrams)>> start;
    private readonly TimeSpan stall;
    private readonly Func<Action<string>?> trace;
    private readonly Action<LiveFeed> retire;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Channel<CameraFrame>> watchers = [];
    private readonly Task running;
    private (CameraFrame Frame, long At)? latest;
    private bool over;
    private int disposed;

    /// <summary>Starts a stream for <paramref name="first"/>, its first watcher.</summary>
    /// <param name="start">Starts the stream: its RTSP connection, played, and the socket its pictures arrive at.</param>
    /// <param name="stall">How long the camera may send nothing before the stream is called lost.</param>
    /// <param name="trace">Where trace lines go, read each time so it can be changed meanwhile.</param>
    /// <param name="retire">Told, once, that this feed is over or that nobody is watching it.</param>
    /// <param name="first">The first watcher's pictures, which see the start's failure if it fails.</param>
    public LiveFeed(
        Func<CancellationToken, Task<(RtspClient Rtsp, ICameraDatagrams Datagrams)>> start,
        TimeSpan stall,
        Func<Action<string>?> trace,
        Action<LiveFeed> retire,
        out Subscription first)
    {
        this.start = start;
        this.stall = stall;
        this.trace = trace;
        this.retire = retire;
        first = TrySubscribe()!;
        running = Task.Run(() => RunAsync(stopping.Token));
    }

    /// <summary>
    /// Starts sending pictures to a new watcher: the latest if it is fresh, then each one that arrives.
    /// </summary>
    /// <returns>The watcher's pictures, or null once this feed is over and another must be started.</returns>
    public Subscription? TrySubscribe()
    {
        var channel = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (watchers)
        {
            if (over)
            {
                return null;
            }

            if (latest is { } last && Environment.TickCount64 - last.At < FreshMilliseconds)
            {
                channel.Writer.TryWrite(last.Frame);
            }

            watchers.Add(channel);
        }

        return new Subscription(this, channel);
    }

    /// <summary>Stops the stream, and waits until its connection and socket are closed. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await running.ConfigureAwait(false);
            stopping.Dispose();
            closed.SetResult();
        }

        await closed.Task.ConfigureAwait(false);
    }

    /// <summary>A copy of a start's failure for one watcher, of a kind that says what went wrong.</summary>
    private static Exception Copy(Exception failure) => failure is TimeoutException
        ? new TimeoutException(failure.Message, failure)
        : new RtspException(failure.Message, failure);

    private void Unsubscribe(Channel<CameraFrame> channel)
    {
        lock (watchers)
        {
            if (!watchers.Remove(channel) || watchers.Count > 0)
            {
                return;
            }

            // Nobody is watching: no one may join a feed on its way out.
            over = true;
        }

        retire(this);
    }

    /// <summary>
    /// Starts the stream, then reads its pictures and watches its connection until one of them ends it, or it
    /// is stopped; then closes both.
    /// </summary>
    private async Task RunAsync(CancellationToken stop)
    {
        RtspClient rtsp;
        ICameraDatagrams datagrams;
        try
        {
            (rtsp, datagrams) = await start(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            End(null);
            return;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            End(() => Copy(failure));
            return;
        }

        try
        {
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var pumping = PumpAsync(datagrams, either.Token);
            var watching = WatchAsync(rtsp, either.Token);
            var first = await Task.WhenAny(pumping, watching).ConfigureAwait(false);
            await either.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pumping, watching).ConfigureAwait(false);
            End(stop.IsCancellationRequested ? null : first.Result);
        }
        finally
        {
            // Closing the connection is what stops the camera sending.
            await rtsp.DisposeAsync().ConfigureAwait(false);
            await datagrams.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The pictures, until the camera goes quiet or the socket fails; then what to tell watchers.</summary>
    private async Task<Func<Exception>?> PumpAsync(ICameraDatagrams datagrams, CancellationToken cancellationToken)
    {
        var reassembler = new RtpJpegReassembler();
        var buffer = new byte[DatagramBuffer];
        var received = 0L;
        try
        {
            while (true)
            {
                var length = await ReceiveAsync(datagrams, buffer, cancellationToken).ConfigureAwait(false);
                if (received++ == 0)
                {
                    trace()?.Invoke($"rtp: first datagram arrived: {Convert.ToHexString(buffer, 0, Math.Min(16, length))}");
                }

                if (reassembler.Push(buffer.AsSpan(0, length)) is not { } frame)
                {
                    if (received == 64 && reassembler.PacketsRead == 0 && trace() is { } noisy)
                    {
                        noisy("rtp: 64 datagrams arrived and none was an RTP/JPEG packet");
                    }

                    continue;
                }

                if (RtpJpegReassembler.LooksLikeJpeg(frame.Jpeg))
                {
                    Publish(frame);
                }
                else
                {
                    trace()?.Invoke($"rtp: a {frame.Jpeg.Length}-byte picture was not a whole JPEG and was skipped");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (TimeoutException quiet)
        {
            trace()?.Invoke("rtp: " + quiet.Message);
            return () => new TimeoutException(quiet.Message);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return () => new RtspException($"The stream was lost: {failure.Message}", failure);
        }
    }

    /// <summary>The stream's connection, until the camera closes it; then what to tell watchers.</summary>
    private async Task<Func<Exception>?> WatchAsync(RtspClient rtsp, CancellationToken cancellationToken)
    {
        try
        {
            await rtsp.WaitForCloseAsync(cancellationToken).ConfigureAwait(false);
            trace()?.Invoke("rtsp: the camera closed the stream");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return () => new RtspException($"The stream was lost: {failure.Message}", failure);
        }
    }

    private async Task<int> ReceiveAsync(ICameraDatagrams datagrams, byte[] buffer, CancellationToken cancellationToken)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quiet.CancelAfter(stall);
        try
        {
            return await datagrams.ReceiveAsync(buffer, quiet.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The camera sent nothing for {stall.TotalSeconds:0} seconds.");
        }
    }

    private void Publish(CameraFrame frame)
    {
        lock (watchers)
        {
            latest = (frame, Environment.TickCount64);
            foreach (var watcher in watchers)
            {
                watcher.Writer.TryWrite(frame);
            }
        }
    }

    /// <summary>Tells every watcher the stream is over: quietly, or with what went wrong.</summary>
    private void End(Func<Exception>? failure)
    {
        bool retiring;
        lock (watchers)
        {
            retiring = !over;
            over = true;
            latest = null;
            foreach (var watcher in watchers)
            {
                watcher.Writer.TryComplete(failure?.Invoke());
            }

            watchers.Clear();
        }

        if (retiring)
        {
            retire(this);
        }
    }

    /// <summary>One watcher's pictures. Disposing it stops them, and ends the feed if nobody else is watching.</summary>
    internal sealed class Subscription(LiveFeed feed, Channel<CameraFrame> channel) : IDisposable
    {
        /// <summary>The next picture, or null once the stream is over without a fault.</summary>
        /// <exception cref="TimeoutException">The stream did not start in time, or the camera went quiet.</exception>
        /// <exception cref="RtspException">The stream could not start, or its connection or socket failed.</exception>
        public async Task<CameraFrame?> NextAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException closed)
            {
                // Each watcher is told with an exception of its own, never thrown before, so throwing it here
                // gives it this watcher's stack.
                return closed.InnerException is { } failure ? throw failure : null;
            }
        }

        /// <inheritdoc />
        public void Dispose() => feed.Unsubscribe(channel);
    }
}
