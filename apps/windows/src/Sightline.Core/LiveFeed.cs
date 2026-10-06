using System.Threading.Channels;
using Sightline.Protocol.Rtp;

namespace Sightline.Core;

/// <summary>
/// The camera's one live stream, read without pause and shared by everybody who wants its pictures.
/// </summary>
/// <remarks>
/// <para>
/// The camera answers one stream connection each time it is switched on, so this is opened once per
/// session and never closed while the session lasts: watching stops and starts by subscribing, not
/// by touching the connection. It is read continuously even with nobody subscribed, so the camera is
/// never left blocked on a connection nobody drains.
/// </para>
/// <para>
/// Each subscriber gets the newest picture: one that has not been taken by the time the next arrives
/// is replaced by it, so a slow subscriber sees fewer pictures, never older ones. A new subscriber
/// starts with the latest picture when it is under a second old, so coming back to the picture shows
/// one at once.
/// </para>
/// </remarks>
internal sealed class LiveFeed : IAsyncDisposable
{
    private readonly RtspClient rtsp;
    private readonly Func<Action<string>?> trace;
    private readonly Func<string, string> ended;
    private const long FreshMilliseconds = 1000;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Channel<CameraFrame>> subscribers = [];
    private readonly Task pump;
    private (CameraFrame Frame, long At)? latest;

    /// <summary>Starts reading <paramref name="rtsp"/>, whose PLAY has been answered.</summary>
    /// <param name="rtsp">The stream connection.</param>
    /// <param name="trace">Where trace lines go, read each time so it can be changed meanwhile.</param>
    /// <param name="ended">Told, once, why the stream ended; returns what subscribers are told.</param>
    public LiveFeed(RtspClient rtsp, Func<Action<string>?> trace, Func<string, string> ended)
    {
        this.rtsp = rtsp;
        this.trace = trace;
        this.ended = ended;
        pump = Task.Run(() => PumpAsync(stopping.Token));
    }

    /// <summary>Starts receiving pictures: the latest if it is fresh, then each one that arrives.</summary>
    /// <remarks>
    /// One that subscribes after the stream has ended hears nothing, and its wait runs out as a quiet
    /// spell; asking the session again then says the picture has gone.
    /// </remarks>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (subscribers)
        {
            if (latest is { } last && Environment.TickCount64 - last.At < FreshMilliseconds)
            {
                channel.Writer.TryWrite(last.Frame);
            }

            subscribers.Add(channel);
        }

        return new Subscription(this, channel);
    }

    /// <summary>Stops reading and closes the connection: only for the end of the session.</summary>
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await pump.ConfigureAwait(false);
        await rtsp.DisposeAsync().ConfigureAwait(false);
        stopping.Dispose();
    }

    private void Unsubscribe(Channel<CameraFrame> channel)
    {
        lock (subscribers)
        {
            subscribers.Remove(channel);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var reason = "The camera ended its live picture.";
        try
        {
            var reassembler = new RtpJpegReassembler();
            var received = 0L;
            while (true)
            {
                var bytes = await rtsp.ReadStreamAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.IsEmpty)
                {
                    trace()?.Invoke($"rtsp: the camera closed the stream after {received} bytes");
                    break;
                }

                if (received == 0)
                {
                    trace()?.Invoke($"rtsp: first stream bytes arrived: {Convert.ToHexString(bytes.Span[..Math.Min(16, bytes.Length)])}");
                }

                received += bytes.Length;
                foreach (var frame in reassembler.Push(bytes.Span))
                {
                    if (RtpJpegReassembler.LooksLikeJpeg(frame.Jpeg))
                    {
                        Publish(frame);
                    }
                    else
                    {
                        trace()?.Invoke($"rtsp: a {frame.Jpeg.Length}-byte frame was not a whole JPEG and was skipped");
                    }
                }

                if (reassembler.PacketsRead == 0 && received > 64 * 1024 && trace() is { } noisy)
                {
                    noisy($"rtsp: {received} bytes arrived but none parsed as an RTP packet");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reason = "The live picture was closed with the camera's session.";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            reason = $"The live picture was lost: {failure.Message}";
        }

        End(reason);
    }

    private void Publish(CameraFrame frame)
    {
        lock (subscribers)
        {
            latest = (frame, Environment.TickCount64);
            foreach (var subscriber in subscribers)
            {
                subscriber.Writer.TryWrite(frame);
            }
        }
    }

    private void End(string reason)
    {
        var told = ended(reason);
        lock (subscribers)
        {
            latest = null;
            foreach (var subscriber in subscribers)
            {
                subscriber.Writer.TryComplete(new LivePictureUnavailableException(told));
            }

            subscribers.Clear();
        }
    }

    /// <summary>One subscriber's pictures. Disposing it stops them, and leaves the stream running.</summary>
    internal sealed class Subscription(LiveFeed feed, Channel<CameraFrame> channel) : IDisposable
    {
        /// <summary>The next picture, waiting at most <paramref name="stall"/> for it.</summary>
        /// <exception cref="TimeoutException">Nothing arrived in time; the stream is still open.</exception>
        /// <exception cref="LivePictureUnavailableException">The stream has ended for good.</exception>
        public async Task<CameraFrame> NextAsync(TimeSpan stall, CancellationToken cancellationToken)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(stall);
            try
            {
                return await channel.Reader.ReadAsync(waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The camera sent nothing for {stall.TotalSeconds:0} seconds.");
            }
            catch (ChannelClosedException closed)
            {
                throw new LivePictureUnavailableException(closed.InnerException!.Message, closed);
            }
        }

        /// <inheritdoc />
        public void Dispose() => feed.Unsubscribe(channel);
    }
}
