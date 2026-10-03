using System.Net;
using System.Net.Sockets;
using Shouldly;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests;

/// <summary>One conversation with one camera, against fakes of both its control channel and its stream.</summary>
public sealed class CameraSessionTests : IAsyncDisposable
{
    private static readonly CameraSessionTiming Quick = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));

    private readonly FakeCamera control = new();
    private readonly List<FakeRtspCamera> streams = [];
    private readonly List<string> trace = [];

    /// <summary>The next RTSP connection the session opens; each stream gets a fresh one, as on the wire.</summary>
    private FakeRtspCamera NextStream(Action<FakeRtspCamera>? configure = null)
    {
        var stream = new FakeRtspCamera { StreamStarted = () => control.IsStreaming };
        configure?.Invoke(stream);
        streams.Add(stream);
        return stream;
    }

    private async Task<CameraSession> OpenAsync()
    {
        var next = 0;
        var session = await CameraSession.OpenAsync(
            port => port == GpSockConnection.Port ? control : streams[next++], "192.168.100.1", Quick);
        session.Trace = trace.Add;
        return session;
    }

    [Fact]
    public async Task A_picture_is_grabbed_after_starting_the_stream_on_the_control_channel()
    {
        var jpeg = FakeRtspCamera.Jpeg(900);
        var stream = NextStream(s => s.Frames.Add(jpeg));
        await using var session = await OpenAsync();

        var frame = await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        frame.Jpeg.ShouldBe(jpeg);
        frame.Width.ShouldBe(640);
        control.IsStreaming.ShouldBeTrue();
        stream.Verbs.ShouldBe(["DESCRIBE", "SETUP", "PLAY", "TEARDOWN"]);
        trace.ShouldContain("control: RestartStreaming acknowledged");
        trace.ShouldContain(line => line.StartsWith("rtsp: first stream bytes arrived: 80", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Frames_keep_coming_until_the_consumer_stops_and_the_session_is_then_torn_down()
    {
        var stream = NextStream(s => s.Frames.AddRange([FakeRtspCamera.Jpeg(100), FakeRtspCamera.Jpeg(200), FakeRtspCamera.Jpeg(300)]));
        await using var session = await OpenAsync();
        var sizes = new List<int>();

        await foreach (var frame in session.StreamFramesAsync())
        {
            sizes.Add(frame.Jpeg.Length);
            if (sizes.Count == 2)
            {
                break;
            }
        }

        sizes.ShouldBe([100, 200]);
        stream.Verbs[^1].ShouldBe("TEARDOWN");
    }

    [Fact]
    public async Task Cancelling_between_frames_ends_the_stream_quietly_and_still_tears_it_down()
    {
        var stream = NextStream(s => s.Frames.AddRange([FakeRtspCamera.Jpeg(100), FakeRtspCamera.Jpeg(200)]));
        await using var session = await OpenAsync();
        using var cancel = new CancellationTokenSource();
        var count = 0;

        await foreach (var _ in session.StreamFramesAsync(cancel.Token))
        {
            count++;
            await cancel.CancelAsync();
        }

        count.ShouldBe(1);
        stream.Verbs[^1].ShouldBe("TEARDOWN");
    }

    [Fact]
    public async Task A_frame_that_is_not_a_whole_jpeg_is_skipped_and_said_so()
    {
        var good = FakeRtspCamera.Jpeg(400);
        NextStream(s => s.Frames.AddRange([[1, 2, 3, 4, 5, 6], good]));
        await using var session = await OpenAsync();

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.ShouldBe(good);
        trace.ShouldContain(line => line.Contains("was not a whole JPEG and was skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_camera_that_closes_the_stream_before_any_picture_is_reported()
    {
        NextStream(s => s.ClosesAfterFrames = true);
        await using var session = await OpenAsync();

        var failed = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        failed.Message.ShouldContain("stopped sending before a whole picture arrived");
        trace.ShouldContain(line => line.StartsWith("rtsp: the camera closed the stream after", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stream_that_goes_quiet_is_called_stopped_rather_than_waited_on()
    {
        // Negotiated, PLAY answered, and nothing ever arrives: the commonest broken-camera state.
        NextStream(s => s.StreamStarted = () => false);
        await using var session = await OpenAsync();

        var stalled = await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in session.StreamFramesAsync())
            {
            }
        });

        stalled.Message.ShouldBe("The camera sent nothing for 0 seconds.");
        streams[0].Verbs[^1].ShouldBe("TEARDOWN");
    }

    [Fact]
    public async Task Grabbing_from_a_quiet_stream_times_out_with_a_sentence()
    {
        NextStream(s => s.StreamStarted = () => false);
        await using var session = await OpenAsync();

        var timedOut = await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromMilliseconds(100)));

        timedOut.Message.ShouldBe("No whole picture arrived within 0 seconds.");
    }

    [Fact]
    public async Task Grabbing_stops_when_the_caller_cancels_rather_than_calling_it_a_timeout()
    {
        NextStream(s => s.StreamStarted = () => false);
        await using var session = await OpenAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task A_server_that_never_answers_the_start_is_called_stuck_and_left_without_a_teardown()
    {
        // SETUP never came back, so there is no session on the camera to end.
        var stream = NextStream(s => s.NeverAnswers = "DESCRIBE");
        await using var session = await OpenAsync();

        var stuck = await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        stuck.InnerException!.Message.ShouldBe("The camera did not start its stream within 0 seconds.");
        stream.Verbs.ShouldBe(["DESCRIBE"]);
    }

    [Theory]
    [InlineData("SETUP", "The camera refused the video track (454).")]
    [InlineData("PLAY", "The camera would not start the stream (454).")]
    public async Task A_refused_step_says_which_and_still_ends_what_was_set_up(string step, string message)
    {
        var stream = NextStream(s =>
        {
            if (step == "SETUP")
            {
                s.SetupStatus = 454;
            }
            else
            {
                s.PlayStatus = 454;
            }
        });
        await using var session = await OpenAsync();

        var refused = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        refused.Message.ShouldBe(message);
        stream.Verbs.Contains("TEARDOWN").ShouldBe(step == "PLAY");
    }

    [Fact]
    public async Task A_teardown_that_fails_does_not_hide_the_picture_that_did_arrive()
    {
        NextStream(s =>
        {
            s.Frames.Add(FakeRtspCamera.Jpeg(500));
            s.TeardownFails = true;
        });
        await using var session = await OpenAsync();

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(500);
    }

    [Fact]
    public async Task Noise_that_is_not_rtp_at_all_is_reported_once_it_passes_64_kilobytes()
    {
        NextStream(s => s.NoiseAfterPlay = new byte[70_000]);
        await using var session = await OpenAsync();

        await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        trace.ShouldContain("rtsp: 70000 bytes arrived but none parsed as an RTP packet");
    }

    [Fact]
    public async Task The_stream_works_with_nobody_tracing_it()
    {
        NextStream(s =>
        {
            s.NoiseAfterPlay = new byte[70_000];
            s.Frames.AddRange([[1, 2, 3, 4, 5, 6], FakeRtspCamera.Jpeg(64)]);
        });
        await using var session = await OpenAsync();
        session.Trace = null;

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(64);
    }

    [Fact]
    public async Task Ending_the_session_closes_the_control_channel()
    {
        var session = await OpenAsync();

        await session.DisposeAsync();

        control.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_opened_without_timings_gets_the_real_ones()
    {
        // The control channel alone: nothing here waits on the stream's ten-second start.
        await using var session = await CameraSession.OpenAsync(_ => control, "192.168.100.1");

        (await session.Control.GetStatusAsync(CancellationToken.None)).Length.ShouldBe(16);
    }

    [Fact]
    public async Task A_camera_that_closes_the_stream_is_handled_with_nobody_tracing_it()
    {
        NextStream(s => s.ClosesAfterFrames = true);
        await using var session = await OpenAsync();
        session.Trace = null;

        await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));
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
    public void Not_being_on_the_cameras_network_is_explained()
    {
        var missing = Should.Throw<CameraNotReachableException>(() =>
            CameraAddress.RequireLocalAddressFor(IPAddress.Parse("203.0.113.1"), [IPAddress.Parse("10.0.0.2")]));

        missing.Message.ShouldContain("Connect to the camera's Wi-Fi first");
        CameraAddress.RequireLocalAddressFor(IPAddress.Parse("192.168.100.1"), [IPAddress.Parse("192.168.100.3")])
            .ShouldBe(IPAddress.Parse("192.168.100.3"));
        new CameraNotReachableException().Message.ShouldNotBeNullOrWhiteSpace();
        new CameraNotReachableException("m", new IOException()).InnerException.ShouldBeOfType<IOException>();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => control.DisposeAsync();

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
