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
/// One conversation with one camera, against fakes of its control channel, its RTSP server and the
/// datagrams its pictures arrive in. Every stream the session starts is a new RTSP connection, handed out
/// in the order the test prepared them.
/// </summary>
public sealed class CameraSessionTests : IAsyncDisposable
{
    private static readonly CameraSessionTiming Quick = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));

    /// <summary>For pictures paced like the real camera's: room for a busy machine between two of them.</summary>
    private static readonly CameraSessionTiming Paced = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    /// <summary>For a start that is never answered, so a test ends it some other way first.</summary>
    private static readonly CameraSessionTiming Patient = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private readonly FakeCamera control = new();
    private readonly Queue<FakeRtspCamera> prepared = new();
    private readonly List<FakeRtspCamera> streams = [];
    private readonly List<string> trace = [];
    private readonly FakeCameraSockets sockets;

    public CameraSessionTests() => sockets = new FakeCameraSockets(Transport);

    [Fact]
    public async Task A_picture_is_grabbed_after_starting_the_stream_on_the_control_channel()
    {
        var jpeg = FakeRtspCamera.Jpeg(900);
        var stream = Next();
        stream.Frames.Add(jpeg);
        await using var session = await OpenAsync();

        var frame = await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        frame.Jpeg.ShouldBe(jpeg);
        frame.Width.ShouldBe(640);
        control.IsStreaming.ShouldBeTrue();
        stream.Verbs.ShouldBe(["DESCRIBE", "SETUP", "PLAY"]);
        var socket = sockets.Opened.ShouldHaveSingleItem();
        stream.Requests[1].ShouldContain($"client_port={socket.Port}-{socket.Port + 1}");
        Traced().ShouldContain("control: RestartStreaming acknowledged");
        Traced().ShouldContain(line => line.StartsWith("rtp: first datagram arrived: 80", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_streams_socket_writes_to_the_cameras_port_first_so_a_firewall_lets_the_pictures_in()
    {
        Next().Frames.Add(FakeRtspCamera.Jpeg(300));
        await using var session = await OpenAsync();

        await session.GrabFrameAsync(TimeSpan.FromSeconds(5));

        var opener = sockets.Opened.ShouldHaveSingleItem().Sent.ShouldHaveSingleItem();
        opener.Port.ShouldBe(FakeRtspCamera.ReferenceServerPort);
        opener.Datagram.ShouldBe(new byte[] { 0 });
        Traced().ShouldContain(line => line.Contains($"from {FakeRtspCamera.ReferenceServerPort} to ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_camera_that_does_not_say_where_it_streams_from_is_written_nothing_and_still_streams()
    {
        var stream = Next();
        stream.SetupTransport = "RTP/AVP;unicast";
        stream.Frames.Add(FakeRtspCamera.Jpeg(300));
        await using var session = await OpenAsync();

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(300);

        sockets.Opened.ShouldHaveSingleItem().Sent.ShouldBeEmpty();
        Traced().ShouldContain(line => line.Contains("from a port it did not say", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stream_stops_once_nobody_is_watching_and_the_next_watcher_starts_another()
    {
        // Closing the stream's connection is what stops the real camera sending.
        Next().Frames.Add(FakeRtspCamera.Jpeg(300));
        Next().Frames.Add(FakeRtspCamera.Jpeg(400));
        await using var session = await OpenAsync();

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(300);
        await EventuallyAsync(() => streams[0].WasDisposed && sockets.Opened[0].IsDisposed);
        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(400);

        streams.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Two_watchers_at_once_share_one_stream()
    {
        var stream = Next();
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
        streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_watcher_who_joins_a_running_stream_sees_its_latest_picture_at_once()
    {
        // The camera sends three pictures and goes quiet; somebody still watching keeps the stream running.
        var last = FakeRtspCamera.Jpeg(333);
        Next().Frames.AddRange([FakeRtspCamera.Jpeg(111), FakeRtspCamera.Jpeg(222), last]);
        await using var session = await OpenAsync(Paced);
        await using var watching = session.StreamFramesAsync().GetAsyncEnumerator();
        while (await watching.MoveNextAsync() && watching.Current.Jpeg.Length != 333)
        {
        }

        watching.Current.Jpeg.Length.ShouldBe(333);

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.ShouldBe(last);
        streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_the_only_watcher_ends_its_pictures_quietly_and_stops_the_stream()
    {
        var stream = Next();
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
        await EventuallyAsync(() => stream.WasDisposed);
    }

    [Fact]
    public async Task A_picture_that_is_not_a_whole_jpeg_is_skipped_and_said_so()
    {
        var good = FakeRtspCamera.Jpeg(400);
        Next().Frames.AddRange([[1, 2, 3, 4, 5, 6], good]);
        await using var session = await OpenAsync();

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.ShouldBe(good);
        Traced().ShouldContain(line => line.Contains("was not a whole JPEG and was skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_camera_ending_its_stream_ends_the_pictures_quietly_and_the_next_watcher_starts_another()
    {
        // Browse mode, on the real camera: it closes the stream's connection, and answers a new one afterwards.
        var first = Next();
        first.Pace = TimeSpan.FromMilliseconds(50);
        first.Frames.AddRange(Enumerable.Range(1, 20).Select(n => FakeRtspCamera.Jpeg(100 + n)));
        Next().Frames.Add(FakeRtspCamera.Jpeg(777));
        await using var session = await OpenAsync(Paced);

        await foreach (var _ in session.StreamFramesAsync())
        {
            first.HangUp();
        }

        Traced().ShouldContain("rtsp: the camera closed the stream");
        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(777);
        streams.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_grab_waiting_when_the_camera_hangs_up_is_told_at_once()
    {
        var stream = Next();
        stream.StreamStarted = () => false;
        await using var session = await OpenAsync(Paced);
        var waiting = session.GrabFrameAsync(TimeSpan.FromSeconds(30));
        // Streaming, not merely asked to: the opener goes out only once PLAY has been answered. Hanging up
        // between asking and the answer is a start that failed, which is another test.
        await EventuallyAsync(() => sockets.Opened.Count > 0 && sockets.Opened[0].Sent.Count > 0);

        stream.HangUp();

        (await Should.ThrowAsync<RtspException>(() => waiting))
            .Message.ShouldBe("The camera ended its stream before a whole picture arrived.");
    }

    [Fact]
    public async Task A_stream_whose_connection_fails_ends_with_why()
    {
        Next().BreaksWith = new IOException("An existing connection was forcibly closed.");
        await using var session = await OpenAsync(Paced);

        var lost = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        lost.Message.ShouldBe("The stream was lost: An existing connection was forcibly closed.");
        lost.InnerException.ShouldBeOfType<IOException>();
    }

    [Fact]
    public async Task A_socket_that_fails_ends_the_stream_with_why()
    {
        var failing = new FailingDatagrams();
        await using var session = await OpenAsync(Paced, new Sockets(sockets.Transport, () => failing));

        var lost = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        lost.Message.ShouldStartWith("The stream was lost: ");
        lost.InnerException.ShouldBeOfType<SocketException>();
        await EventuallyAsync(() => failing.Disposed);
    }

    [Fact]
    public async Task A_quiet_camera_ends_the_stream_and_watching_again_starts_another()
    {
        var quiet = Next();
        quiet.StreamStarted = () => false;
        Next().Frames.Add(FakeRtspCamera.Jpeg(500));
        await using var session = await OpenAsync();

        var silence = await Should.ThrowAsync<TimeoutException>(() => WatchToTheEndAsync(session));

        silence.Message.ShouldBe("The camera sent nothing for 0 seconds.");
        Traced().ShouldContain("rtp: The camera sent nothing for 0 seconds.");
        await EventuallyAsync(() => quiet.WasDisposed);
        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(500);
        streams.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Grabbing_from_a_quiet_stream_times_out_with_a_sentence()
    {
        Next().StreamStarted = () => false;
        await using var session = await OpenAsync(Paced);

        var timedOut = await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromMilliseconds(100)));

        timedOut.Message.ShouldBe("No whole picture arrived within 0 seconds.");
    }

    [Fact]
    public async Task Grabbing_stops_when_the_caller_cancels_rather_than_calling_it_a_timeout()
    {
        Next().StreamStarted = () => false;
        await using var session = await OpenAsync(Paced);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task A_start_the_camera_never_answers_times_out_closes_what_it_opened_and_is_tried_afresh_next_time()
    {
        var stuck = Next();
        stuck.NeverAnswers = "DESCRIBE";
        Next().Frames.Add(FakeRtspCamera.Jpeg(500));
        await using var session = await OpenAsync();

        var timedOut = await Should.ThrowAsync<TimeoutException>(() => WatchToTheEndAsync(session));

        timedOut.Message.ShouldBe("The camera did not start its stream within 0 seconds.");
        stuck.WasDisposed.ShouldBeTrue();
        sockets.Opened[0].IsDisposed.ShouldBeTrue();
        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(500);
    }

    [Theory]
    [InlineData("SETUP", "The camera refused the video track (454).")]
    [InlineData("PLAY", "The camera would not start the stream (454).")]
    public async Task A_refused_step_says_which_and_closes_what_was_opened(string step, string message)
    {
        var stream = Next();
        if (step == "SETUP")
        {
            stream.SetupStatus = 454;
        }
        else
        {
            stream.PlayStatus = 454;
        }

        await using var session = await OpenAsync();

        var refused = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        refused.Message.ShouldBe(message);
        refused.InnerException.ShouldBeOfType<RtspException>();
        stream.WasDisposed.ShouldBeTrue();
        sockets.Opened.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_start_reaches_everyone_waiting_on_it()
    {
        Next().SetupStatus = 454;
        await using var session = await OpenAsync();

        // Slow enough answers that both are waiting before the start gets as far as SETUP.
        control.AnswerDelay = TimeSpan.FromMilliseconds(200);
        var first = Should.ThrowAsync<RtspException>(() => WatchToTheEndAsync(session));
        var second = Should.ThrowAsync<RtspException>(() => WatchToTheEndAsync(session));

        (await first).Message.ShouldBe("The camera refused the video track (454).");
        (await second).Message.ShouldBe("The camera refused the video track (454).");
        streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_watcher_who_leaves_while_the_stream_is_starting_stops_the_start()
    {
        var stuck = Next();
        stuck.NeverAnswers = "DESCRIBE";
        await using var session = await OpenAsync(Patient);

        await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromMilliseconds(200)));

        // Long before the thirty seconds the start was given.
        await EventuallyAsync(() => stuck.WasDisposed && sockets.Opened[0].IsDisposed);
    }

    [Fact]
    public async Task A_socket_that_cannot_be_opened_fails_the_start_and_closes_the_connection()
    {
        var stream = Next();
        await using var session = await OpenAsync(Quick, new Sockets(sockets.Transport, () => throw new SocketException((int)SocketError.AddressNotAvailable)));

        var failed = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        failed.InnerException.ShouldBeOfType<SocketException>();
        stream.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_connection_that_cannot_be_made_fails_the_start_with_nothing_left_open()
    {
        var opened = 0;
        ICameraTransport Refuse(int port) => port == GpSockConnection.Port
            ? control
            : throw new SocketException((int)SocketError.ConnectionRefused);
        await using var session = await OpenAsync(Quick, new Sockets(Refuse, () =>
        {
            opened++;
            return new FailingDatagrams();
        }));

        var failed = await Should.ThrowAsync<RtspException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(5)));

        failed.InnerException.ShouldBeOfType<SocketException>();
        opened.ShouldBe(0);
    }

    [Fact]
    public async Task Noise_that_is_not_rtp_at_all_is_reported_once_64_datagrams_of_it_have_arrived()
    {
        Next().NoiseAfterPlay.AddRange(Enumerable.Repeat(new byte[] { 1, 2, 3 }, 64));
        await using var session = await OpenAsync(Paced);

        await Should.ThrowAsync<TimeoutException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(1)));

        Traced().ShouldContain("rtp: 64 datagrams arrived and none was an RTP/JPEG packet");
    }

    [Fact]
    public async Task Noise_after_the_stream_has_shown_itself_to_be_rtp_is_not_called_a_stream_of_noise()
    {
        Next().Frames.Add(FakeRtspCamera.Jpeg(200));
        await using var session = await OpenAsync(Paced);
        await using var watching = session.StreamFramesAsync().GetAsyncEnumerator();
        (await watching.MoveNextAsync()).ShouldBeTrue();
        var socket = sockets.Opened.ShouldHaveSingleItem();

        for (var i = 0; i < 63; i++)
        {
            socket.Deliver([1, 2, 3]);
        }

        socket.Deliver(FakeRtspCamera.Packet(FakeRtspCamera.Jpeg(201), 1));
        (await watching.MoveNextAsync()).ShouldBeTrue();

        watching.Current.Jpeg.Length.ShouldBe(201);
        Traced().ShouldNotContain(line => line.Contains("none was an RTP/JPEG packet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stream_works_with_nobody_tracing_it()
    {
        // Noise, a broken picture and a good one; then a stream that goes quiet; then one the camera ends.
        var noisy = Next();
        noisy.NoiseAfterPlay.AddRange(Enumerable.Repeat(new byte[] { 1, 2, 3 }, 64));
        noisy.Frames.AddRange([[1, 2, 3, 4, 5, 6], FakeRtspCamera.Jpeg(64)]);
        Next().StreamStarted = () => false;
        var ending = Next();
        ending.Frames.Add(FakeRtspCamera.Jpeg(65));
        ending.ClosesAfterFrames = true;
        await using var session = await OpenAsync();
        session.Trace = null;

        (await session.GrabFrameAsync(TimeSpan.FromSeconds(5))).Jpeg.Length.ShouldBe(64);
        await EventuallyAsync(() => noisy.WasDisposed);
        await Should.ThrowAsync<TimeoutException>(() => WatchToTheEndAsync(session));
        (await WatchToTheEndAsync(session)).ShouldBe(1);

        Traced().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ending_the_session_ends_every_watchers_pictures_and_closes_the_stream_and_the_control_channel()
    {
        var stream = Next();
        stream.Pace = TimeSpan.FromMilliseconds(30);
        stream.Frames.AddRange(Enumerable.Range(1, 100).Select(n => FakeRtspCamera.Jpeg(100 + n)));
        var session = await OpenAsync(Paced);
        await using var watching = session.StreamFramesAsync().GetAsyncEnumerator();
        (await watching.MoveNextAsync()).ShouldBeTrue();

        await session.DisposeAsync();

        stream.WasDisposed.ShouldBeTrue();
        sockets.Opened.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
        control.WasDisposed.ShouldBeTrue();
        while (await watching.MoveNextAsync())
        {
            // A picture already on its way may still come; then the pictures end, quietly.
        }
    }

    [Fact]
    public async Task Ending_the_session_while_the_stream_is_starting_stops_the_start_and_tells_a_grab_why()
    {
        var stuck = Next();
        stuck.NeverAnswers = "DESCRIBE";
        var session = await OpenAsync(Patient);
        var grabbing = session.GrabFrameAsync(TimeSpan.FromSeconds(30));
        await EventuallyAsync(() => stuck.Verbs.Contains("DESCRIBE"));

        await session.DisposeAsync();

        (await Should.ThrowAsync<ObjectDisposedException>(() => grabbing))
            .Message.ShouldStartWith("The session closed before a whole picture arrived.");
        stuck.WasDisposed.ShouldBeTrue();
        control.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_that_has_ended_starts_no_stream_and_closed_only_its_control_channel()
    {
        var session = await OpenAsync();

        await session.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => session.GrabFrameAsync(TimeSpan.FromSeconds(1)));
        await Should.ThrowAsync<ObjectDisposedException>(() => WatchToTheEndAsync(session));
        streams.ShouldBeEmpty();
        control.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_opened_without_timings_gets_the_real_ones()
    {
        // The control channel alone: nothing here waits on the stream's ten-second start.
        await using var session = await CameraSession.OpenAsync(new FakeCameraSockets(_ => control), "192.168.100.1");

        (await session.Control.GetStatusAsync(CancellationToken.None)).Length.ShouldBe(16);
        CameraSessionTiming.Default.ShouldBe(new CameraSessionTiming(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task A_control_channel_that_cannot_open_is_closed_again_and_reported()
    {
        var refusing = new RefusingTransport();

        await Should.ThrowAsync<SocketException>(() => CameraSession.OpenAsync(new FakeCameraSockets(_ => refusing), "192.168.100.1"));

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
        await Should.ThrowAsync<ArgumentNullException>(() => CameraSession.OpenAsync((ICameraSockets)null!, "host"));
        await Should.ThrowAsync<ArgumentException>(() => CameraSession.OpenAsync(sockets, " "));
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
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await control.DisposeAsync();
        foreach (var stream in streams)
        {
            await stream.DisposeAsync();
        }
    }

    private static async Task<int> WatchToTheEndAsync(CameraSession session)
    {
        var count = 0;
        await foreach (var _ in session.StreamFramesAsync())
        {
            count++;
        }

        return count;
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

    /// <summary>The next stream the camera will answer, for the test to set up before it is opened.</summary>
    private FakeRtspCamera Next()
    {
        var stream = new FakeRtspCamera { StreamStarted = () => control.IsStreaming };
        prepared.Enqueue(stream);
        return stream;
    }

    private ICameraTransport Transport(int port)
    {
        if (port == GpSockConnection.Port)
        {
            return control;
        }

        var stream = prepared.TryDequeue(out var ready) ? ready : new FakeRtspCamera { StreamStarted = () => control.IsStreaming };
        lock (streams)
        {
            streams.Add(stream);
        }

        return stream;
    }

    private async Task<CameraSession> OpenAsync(CameraSessionTiming? timing = null, ICameraSockets? through = null)
    {
        var session = await CameraSession.OpenAsync(through ?? sockets, "192.168.100.1", timing ?? Quick);
        session.Trace = line =>
        {
            lock (trace)
            {
                trace.Add(line);
            }
        };
        return session;
    }

    private List<string> Traced()
    {
        lock (trace)
        {
            return [.. trace];
        }
    }

    /// <summary>Connections and sockets that come from the test.</summary>
    private sealed class Sockets(Func<int, ICameraTransport> transports, Func<ICameraDatagrams> datagrams) : ICameraSockets
    {
        public ICameraTransport Transport(int port) => transports(port);

        public ICameraDatagrams Datagrams() => datagrams();
    }

    /// <summary>A socket whose every receive fails, as one on an adapter that has gone does.</summary>
    private sealed class FailingDatagrams : ICameraDatagrams
    {
        public int Port => 50999;

        public bool Disposed { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> datagram, int port, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken) =>
            Task.FromException<int>(new SocketException((int)SocketError.NetworkReset));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
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
