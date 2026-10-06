using System.Net;
using System.Net.Sockets;
using Shouldly;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests;

/// <summary>
/// One conversation with one camera, against fakes of both its control channel and its stream. The
/// session may open one stream connection: a second one fails the test, as on the reference camera
/// it would never be answered.
/// </summary>
public sealed class CameraSessionTests : IAsyncDisposable
{
    private static readonly CameraSessionTiming Quick = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));

    /// <summary>For pictures paced like the real camera's: room for a busy machine between two of them.</summary>
    private static readonly CameraSessionTiming Paced = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    private readonly FakeCamera control = new();
    private readonly FakeRtspCamera stream = new();
    private readonly List<string> trace = [];
    private int streamsOpened;

    public CameraSessionTests() => stream.StreamStarted = () => control.IsStreaming;

    private async Task<CameraSession> OpenAsync(CameraSessionTiming? timing = null)
    {
        var session = await CameraSession.OpenAsync(Transport, "192.168.100.1", timing ?? Quick);
        session.Trace = trace.Add;
        return session;
    }

    private ICameraTransport Transport(int port)
    {
        if (port == GpSockConnection.Port)
        {
            return control;
        }

        Interlocked.Increment(ref streamsOpened).ShouldBe(1, "a second stream connection is never answered by the camera");
        return stream;
    }

    [Fact]
    public async Task A_picture_is_grabbed_after_starting_the_stream_on_the_control_channel()
    {
        var jpeg = FakeRtspCamera.Jpeg(900);
        stream.Frames.Add(jpeg);
        await using var session = await OpenAsync();

        var frame = await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        frame.Jpeg.ShouldBe(jpeg);
        frame.Width.ShouldBe(640);
        control.IsStreaming.ShouldBeTrue();
        stream.Verbs.ShouldBe(["DESCRIBE", "SETUP", "PLAY"]);
        trace.ShouldContain("control: RestartStreaming acknowledged");
        trace.ShouldContain(line => line.StartsWith("rtsp: first stream bytes arrived: 80", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stream_is_opened_once_and_kept_however_many_times_pictures_are_wanted()
    {
        stream.Pace = TimeSpan.FromMilliseconds(30);
        stream.Frames.AddRange(Enumerable.Range(1, 20).Select(n => FakeRtspCamera.Jpeg(100 + n)));
        await using var session = await OpenAsync(Paced);
        session.HoldsLivePicture.ShouldBeFalse();

        await session.GrabFrameAsync(TimeSpan.FromSeconds(5));
        await foreach (var _ in session.StreamFramesAsync())
        {
            break;
        }

        await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        streamsOpened.ShouldBe(1);
        stream.Verbs.ShouldBe(["DESCRIBE", "SETUP", "PLAY"]);
        stream.IsConnected.ShouldBeTrue();
        session.HoldsLivePicture.ShouldBeTrue();
    }

    [Fact]
    public async Task Two_watchers_at_once_share_the_one_stream()
    {
        stream.Pace = TimeSpan.FromMilliseconds(30);
        stream.Frames.AddRange(Enumerable.Range(1, 30).Select(n => FakeRtspCamera.Jpeg(100 + n)));
        await using var session = await OpenAsync(Paced);

        async Task<int> Watch()
        {
            var count = 0;
            await foreach (var _ in session.StreamFramesAsync())
            {
                if (++count == 3)
                {
                    break;
                }
            }

            return count;
        }

        (await Task.WhenAll(Watch(), Watch())).ShouldBe([3, 3]);
        streamsOpened.ShouldBe(1);
    }

    [Fact]
    public async Task Someone_who_comes_back_to_the_picture_sees_the_latest_one_at_once()
    {
        // Every picture arrives before anybody is watching, and the camera then goes quiet.
        var last = FakeRtspCamera.Jpeg(333);
        stream.Frames.AddRange([FakeRtspCamera.Jpeg(111), FakeRtspCamera.Jpeg(222), last]);
        await using var session = await OpenAsync();
        await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.ShouldBe(last);
    }

    [Fact]
    public async Task Cancelling_a_watcher_ends_its_pictures_quietly_and_leaves_the_stream_open()
    {
        stream.Pace = TimeSpan.FromMilliseconds(30);
        stream.Frames.AddRange(Enumerable.Range(1, 10).Select(n => FakeRtspCamera.Jpeg(100 + n)));
        await using var session = await OpenAsync(Paced);
        using var cancel = new CancellationTokenSource();
        var count = 0;

        await foreach (var _ in session.StreamFramesAsync(cancel.Token))
        {
            count++;
            await cancel.CancelAsync();
        }

        count.ShouldBe(1);
        stream.IsConnected.ShouldBeTrue();
        session.HoldsLivePicture.ShouldBeTrue();
    }

    [Fact]
    public async Task A_frame_that_is_not_a_whole_jpeg_is_skipped_and_said_so()
    {
        var good = FakeRtspCamera.Jpeg(400);
        stream.Frames.AddRange([[1, 2, 3, 4, 5, 6], good]);
        await using var session = await OpenAsync();

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.ShouldBe(good);
        trace.ShouldContain(line => line.Contains("was not a whole JPEG and was skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_camera_that_ends_its_stream_has_given_its_picture_for_this_power_on()
    {
        stream.Frames.Add(FakeRtspCamera.Jpeg(500));
        stream.Pace = TimeSpan.FromMilliseconds(100);
        await using var session = await OpenAsync();
        await foreach (var _ in session.StreamFramesAsync())
        {
            // Browse mode, on the real camera.
            stream.HangUp();
            break;
        }

        var gone = await Should.ThrowAsync<LivePictureUnavailableException>(async () =>
        {
            await foreach (var _ in session.StreamFramesAsync())
            {
            }
        });

        gone.Message.ShouldBe("The camera ended its live picture. " + LivePictureUnavailableException.Advice);
        trace.ShouldContain(line => line.StartsWith("rtsp: the camera closed the stream after", StringComparison.Ordinal));
        session.HoldsLivePicture.ShouldBeFalse();
        (await Should.ThrowAsync<LivePictureUnavailableException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5))))
            .Message.ShouldBe(gone.Message);
        streamsOpened.ShouldBe(1);
    }

    [Fact]
    public async Task A_watcher_waiting_when_the_camera_hangs_up_is_told_at_once()
    {
        await using var session = await OpenAsync();
        stream.StreamStarted = () => false;
        var waiting = session.GrabFrameAsync(TimeSpan.FromSeconds(30));
        await EventuallyAsync(() => stream.Verbs.Contains("PLAY"));
        // Long enough for the grab, which has no picture to take, to be subscribed and waiting.
        await Task.Delay(200);

        stream.HangUp();

        (await Should.ThrowAsync<LivePictureUnavailableException>(() => waiting))
            .InnerException.ShouldBeOfType<System.Threading.Channels.ChannelClosedException>();
    }

    [Fact]
    public async Task A_stream_that_drops_is_reported_with_why()
    {
        stream.BreaksWith = new IOException("An existing connection was forcibly closed.");
        await using var session = await OpenAsync();

        var lost = await Should.ThrowAsync<LivePictureUnavailableException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        lost.Message.ShouldStartWith("The live picture was lost: An existing connection was forcibly closed.");
    }

    [Fact]
    public async Task A_quiet_spell_is_said_and_the_same_stream_is_watched_again()
    {
        // Negotiated, PLAY answered, and nothing arrives: the stream stays open, and is waited on again.
        stream.StreamStarted = () => false;
        await using var session = await OpenAsync();
        async Task Watch()
        {
            await foreach (var _ in session.StreamFramesAsync())
            {
            }
        }

        var quiet = await Should.ThrowAsync<TimeoutException>(Watch);
        await Should.ThrowAsync<TimeoutException>(Watch);

        quiet.Message.ShouldBe("The camera sent nothing for 0 seconds.");
        session.HoldsLivePicture.ShouldBeTrue();
        streamsOpened.ShouldBe(1);
        stream.Verbs.ShouldBe(["DESCRIBE", "SETUP", "PLAY"]);
    }

    [Fact]
    public async Task Grabbing_from_a_quiet_stream_times_out_with_a_sentence()
    {
        stream.StreamStarted = () => false;
        await using var session = await OpenAsync();

        var timedOut = await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromMilliseconds(100)));

        timedOut.Message.ShouldBe("No whole picture arrived within 0 seconds.");
    }

    [Fact]
    public async Task Grabbing_stops_when_the_caller_cancels_rather_than_calling_it_a_timeout()
    {
        stream.StreamStarted = () => false;
        await using var session = await OpenAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task A_camera_that_never_answers_the_start_has_given_its_picture_elsewhere_and_is_not_asked_again()
    {
        // What the reference camera does with any connection after its first since power-on.
        stream.NeverAnswers = "DESCRIBE";
        await using var session = await OpenAsync();

        var stuck = await Should.ThrowAsync<LivePictureUnavailableException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        stuck.Message.ShouldBe(
            "The camera did not start its live picture within 0 seconds, which is what it does once it has given its "
            + "live picture to an earlier connection. " + LivePictureUnavailableException.Advice);
        stuck.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        await Should.ThrowAsync<LivePictureUnavailableException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));
        stream.Verbs.ShouldBe(["DESCRIBE"]);
        stream.IsConnected.ShouldBeFalse();
    }

    [Theory]
    [InlineData("SETUP", "The camera refused the video track (454).")]
    [InlineData("PLAY", "The camera would not start the stream (454).")]
    public async Task A_refused_step_says_which(string step, string message)
    {
        if (step == "SETUP")
        {
            stream.SetupStatus = 454;
        }
        else
        {
            stream.PlayStatus = 454;
        }

        await using var session = await OpenAsync();

        var refused = await Should.ThrowAsync<LivePictureUnavailableException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        refused.Message.ShouldBe(message + " " + LivePictureUnavailableException.Advice);
        refused.InnerException.ShouldBeOfType<RtspException>();
    }

    [Fact]
    public async Task Noise_that_is_not_rtp_at_all_is_reported_once_it_passes_64_kilobytes()
    {
        stream.NoiseAfterPlay = new byte[70_000];
        await using var session = await OpenAsync();

        await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromMilliseconds(500)));

        trace.ShouldContain("rtsp: 70000 bytes arrived but none parsed as an RTP packet");
    }

    [Fact]
    public async Task The_stream_works_with_nobody_tracing_it()
    {
        stream.NoiseAfterPlay = new byte[70_000];
        stream.Frames.AddRange([[1, 2, 3, 4, 5, 6], FakeRtspCamera.Jpeg(64)]);
        stream.ClosesAfterFrames = true;
        stream.Pace = TimeSpan.FromMilliseconds(50);
        await using var session = await OpenAsync();
        session.Trace = null;

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(64);
        await Should.ThrowAsync<LivePictureUnavailableException>(async () =>
        {
            await foreach (var _ in session.StreamFramesAsync())
            {
            }
        });
    }

    [Fact]
    public async Task Ending_the_session_closes_the_stream_and_then_the_control_channel()
    {
        stream.Frames.Add(FakeRtspCamera.Jpeg(500));
        var session = await OpenAsync();
        await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync();

        stream.IsConnected.ShouldBeFalse();
        control.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Ending_the_session_while_the_stream_is_starting_ends_the_start_too()
    {
        stream.NeverAnswers = "DESCRIBE";
        var session = await CameraSession.OpenAsync(Transport, "192.168.100.1", new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
        var grabbing = session.GrabFrameAsync(TimeSpan.FromSeconds(30));
        await EventuallyAsync(() => stream.Verbs.Contains("DESCRIBE"));

        await session.DisposeAsync();

        (await Should.ThrowAsync<LivePictureUnavailableException>(() => grabbing))
            .Message.ShouldStartWith("The session ended before the live picture started.");
        control.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Ending_a_session_that_never_streamed_closes_only_the_control_channel()
    {
        var session = await OpenAsync();

        await session.DisposeAsync();

        control.WasDisposed.ShouldBeTrue();
        streamsOpened.ShouldBe(0);
    }

    [Fact]
    public async Task A_session_opened_without_timings_gets_the_real_ones()
    {
        // The control channel alone: nothing here waits on the stream's ten-second start.
        await using var session = await CameraSession.OpenAsync(_ => control, "192.168.100.1");

        (await session.Control.GetStatusAsync(CancellationToken.None)).Length.ShouldBe(16);
    }

    [Fact]
    public async Task A_control_channel_that_cannot_open_is_closed_again_and_reported()
    {
        var refusing = new RefusingTransport();

        await Should.ThrowAsync<SocketException>(() => CameraSession.OpenAsync(_ => refusing, "192.168.100.1"));

        refusing.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_real_camera_that_is_not_there_refuses_the_connection()
    {
        // Loopback only: nothing listens on 127.0.0.2:8081, so this fails fast and touches no adapter.
        await Should.ThrowAsync<SocketException>(() =>
            CameraSession.OpenAsync(IPAddress.Parse("127.0.0.2"), IPAddress.Loopback));
    }

    [Fact]
    public async Task The_arguments_are_checked()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => CameraSession.OpenAsync(null!, IPAddress.Loopback));
        await Should.ThrowAsync<ArgumentNullException>(() => CameraSession.OpenAsync(IPAddress.Loopback, null!));
        await Should.ThrowAsync<ArgumentNullException>(() => CameraSession.OpenAsync(null!, "host"));
        await Should.ThrowAsync<ArgumentException>(() => CameraSession.OpenAsync(_ => control, " "));
        CameraSessionTiming.Default.Stall.ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void The_exceptions_say_something_however_they_are_made()
    {
        var missing = Should.Throw<CameraNotReachableException>(() =>
            CameraAddress.RequireLocalAddressFor(IPAddress.Parse("203.0.113.1"), [IPAddress.Parse("10.0.0.2")]));

        missing.Message.ShouldContain("Connect to the camera's Wi-Fi first");
        CameraAddress.RequireLocalAddressFor(IPAddress.Parse("192.168.100.1"), [IPAddress.Parse("192.168.100.3")])
            .ShouldBe(IPAddress.Parse("192.168.100.3"));
        new CameraNotReachableException().Message.ShouldNotBeNullOrWhiteSpace();
        new CameraNotReachableException("m", new IOException()).InnerException.ShouldBeOfType<IOException>();
        new LivePictureUnavailableException().Message.ShouldBe(LivePictureUnavailableException.Advice);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await control.DisposeAsync();
        await stream.DisposeAsync();
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition never held");
            await Task.Delay(10);
        }
    }

    private sealed class RefusingTransport : ICameraTransport
    {
        public bool Disposed { get; private set; }

        public bool IsConnected => false;

        public Task ConnectAsync(CancellationToken cancellationToken) => throw new SocketException((int)SocketError.ConnectionRefused);

        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken) => Task.FromResult(0);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
