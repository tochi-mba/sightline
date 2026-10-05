using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Testing;
using Sightline.Protocol.GpSock;
using Sightline.Protocol;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>Connecting to the camera, staying connected, and getting it back when it is lost.</summary>
public sealed class ConnectingTests
{
    [Fact]
    public async Task Connecting_joins_the_camera_opens_it_and_reads_it()
    {
        await using var camera = new ControllerHarness();
        camera.Control.Values[MenuIds.RecordResolution] = [2];

        var state = await camera.ConnectedAsync();

        camera.Seen.ShouldContain(s => s.Connection is Connection.Joining);
        camera.Seen.ShouldContain(s => s.Connection is Connection.Opening);
        state.CameraName.ShouldBe("ActionCam_000000000000");
        state.Mode.ShouldBe(CaptureMode.Video);
        state.Settings.Count.ShouldBe(21);
        state.Settings.Single(s => s.Menu.Id == MenuIds.RecordResolution).Value.ShouldBe(2);
        state.Settings.Single(s => s.Menu.Name == "Format").Shown.ShouldBeNull();
        state.Settings.Single(s => s.Menu.Name == "Version").Text.ShouldBeNull();
        state.Settings.Single(s => s.Menu.Id == MenuIds.WifiPassword).Text.ShouldBe("12345678");
    }

    [Fact]
    public async Task A_camera_in_photo_mode_connects_in_photo_mode_and_one_left_browsing_is_put_back()
    {
        await using var photo = new ControllerHarness();
        photo.Control.Mode = CameraMode.Capture;
        (await photo.ConnectedAsync()).Mode.ShouldBe(CaptureMode.Photo);

        await using var browsing = new ControllerHarness();
        browsing.Control.Mode = CameraMode.Browse;
        (await browsing.ConnectedAsync()).Mode.ShouldBe(CaptureMode.Video);
        browsing.Control.Mode.ShouldBe(CameraMode.Record);
    }

    [Fact]
    public async Task A_camera_already_recording_shows_as_recording_and_its_own_button_is_seen()
    {
        await using var camera = new ControllerHarness();
        camera.Control.IsRecording = true;
        (await camera.ConnectedAsync()).IsRecording.ShouldBeTrue();

        camera.Control.IsRecording = false;

        (await camera.UntilAsync(s => !s.IsRecording)).IsRecording.ShouldBeFalse();
    }

    [Fact]
    public async Task Asking_to_connect_while_connecting_returns_the_same_connection()
    {
        await using var camera = new ControllerHarness();
        var first = camera.Controller.Connect();

        camera.Controller.Connect().ShouldBeSameAs(first);
        await camera.UntilAsync(s => s.IsConnected);
        camera.Link.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_network_this_pc_cannot_join_is_explained_at_once()
    {
        await using var camera = new ControllerHarness();
        camera.Link.Failures.Enqueue(new CameraLinkException("No adapter can see it."));

        await camera.Controller.Connect();

        var failed = camera.State.Connection.ShouldBeOfType<Connection.Failed>();
        failed.Problem.Kind.ShouldBe(ProblemKind.NotJoined);
        failed.Problem.Remedy.ShouldBe(Remedy.ChooseAgain);
        failed.Problem.Detail.ShouldBe("No adapter can see it.");
        camera.Link.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_control_port_that_never_answers_is_reported_and_its_network_let_go()
    {
        await using var camera = new ControllerHarness();
        camera.Link.ControlTransport = () => new Unanswering();

        await camera.Controller.Connect();

        var failed = camera.State.Connection.ShouldBeOfType<Connection.Failed>();
        failed.Problem.Kind.ShouldBe(ProblemKind.NoAnswer);
        failed.Problem.Detail.ShouldContain("did not answer on its control port");
        camera.Link.Lease.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_control_port_that_refuses_is_reported_as_not_answering()
    {
        await using var camera = new ControllerHarness();
        camera.Link.ControlTransport = () => new Refusing();

        await camera.Controller.Connect();

        camera.State.Connection.ShouldBeOfType<Connection.Failed>().Problem.Kind.ShouldBe(ProblemKind.NoAnswer);
    }

    [Fact]
    public async Task A_camera_that_hangs_up_while_being_read_is_reported_as_lost()
    {
        await using var camera = new ControllerHarness();
        camera.Control.HangsUp = true;

        await camera.Controller.Connect();

        camera.State.Connection.ShouldBeOfType<Connection.Failed>().Problem.Kind.ShouldBe(ProblemKind.Lost);
        camera.Control.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Losing_the_network_reconnects()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();
        var first = camera.Link.Lease;

        first.Lose();
        var reconnecting = (await camera.UntilAsync(s => s.Connection is Connection.Reconnecting)).Connection.ShouldBeOfType<Connection.Reconnecting>();
        await camera.UntilAsync(s => s.IsConnected);

        reconnecting.Attempt.ShouldBe(1);
        reconnecting.Of.ShouldBe(ControllerHarness.Quick.ReconnectAttempts);
        reconnecting.Problem.Kind.ShouldBe(ProblemKind.Lost);
        first.Disposed.ShouldBeTrue();
        camera.Link.Requests.ShouldBe([false, true]);
    }

    [Fact]
    public async Task A_camera_that_stops_answering_a_poll_is_lost_and_reconnected()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();

        camera.Control.GoesQuiet = true;
        var reconnecting = (await camera.UntilAsync(s => s.Connection is Connection.Reconnecting)).Connection.ShouldBeOfType<Connection.Reconnecting>();
        camera.Control.GoesQuiet = false;
        await camera.UntilAsync(s => s.IsConnected);

        reconnecting.Problem.Kind.ShouldBe(ProblemKind.NoAnswer);
    }

    [Fact]
    public async Task A_refused_poll_is_asked_again_rather_than_taken_for_a_lost_camera()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();
        camera.Control.ForcedRefusals[GpSockCommand.GetDeviceStatus] = NakCode.ServerBusy;

        await Task.Delay(400);
        camera.Control.ForcedRefusals.Clear();
        camera.Control.IsRecording = true;

        (await camera.UntilAsync(s => s.IsRecording)).Connection.ShouldBeOfType<Connection.Connected>();
    }

    [Fact]
    public async Task A_camera_that_never_comes_back_is_given_up_with_the_last_reason()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();
        for (var i = 0; i < 3; i++)
        {
            camera.Link.Failures.Enqueue(new CameraLinkException("Not in range."));
        }

        camera.Link.Lease.Lose();
        var failed = (await camera.UntilAsync(s => s.Connection is Connection.Failed)).Connection.ShouldBeOfType<Connection.Failed>();

        failed.Problem.Kind.ShouldBe(ProblemKind.NotJoined);
        camera.Link.Requests.Count.ShouldBe(4);
        camera.Seen.Select(s => (s.Connection as Connection.Reconnecting)?.Attempt).OfType<int>().Distinct().ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task With_reconnecting_switched_off_a_lost_camera_is_reported_at_once()
    {
        await using var camera = new ControllerHarness(reconnects: false);
        await camera.ConnectedAsync();

        camera.Link.Lease.Lose();

        (await camera.UntilAsync(s => s.Connection is Connection.Failed)).Connection
            .ShouldBeOfType<Connection.Failed>().Problem.Kind.ShouldBe(ProblemKind.Lost);
        camera.Link.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Disconnecting_leaves_the_camera_and_forgets_it_but_not_the_mode()
    {
        await using var camera = new ControllerHarness();
        camera.Control.Mode = CameraMode.Capture;
        await camera.ConnectedAsync();

        await camera.Controller.DisconnectAsync();

        camera.State.ShouldBe(CameraState.Initial with { Mode = CaptureMode.Photo });
        camera.Control.WasDisposed.ShouldBeTrue();
        camera.Link.Lease.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Disconnecting_while_joining_or_before_connecting_is_harmless()
    {
        await using var camera = new ControllerHarness();
        await camera.Controller.DisconnectAsync();
        camera.Link.Waits = true;
        _ = camera.Controller.Connect();
        await camera.UntilAsync(s => s.Connection is Connection.Joining);

        await camera.Controller.DisconnectAsync();

        camera.State.Connection.ShouldBeOfType<Connection.Idle>();
        camera.Link.Leases.ShouldBeEmpty();
    }

    [Fact]
    public async Task Asking_before_connecting_says_to_connect_and_while_reconnecting_to_wait()
    {
        await using var camera = new ControllerHarness();
        camera.Controller.TakePhoto().ShouldBeNull();
        camera.State.Notice!.Text.ShouldBe("Connect to the camera first.");

        await camera.ConnectedAsync();
        camera.Link.Failures.Enqueue(new CameraLinkException("Not yet."));
        camera.Link.Lease.Lose();
        await camera.UntilAsync(s => s.Connection is Connection.Reconnecting);
        camera.Controller.ToggleRecording().ShouldBeNull();

        camera.State.Notice!.Text.ShouldBe("The camera is reconnecting. Try again in a moment.");
    }

    [Fact]
    public async Task A_notice_is_dismissed_only_by_its_own_number()
    {
        await using var camera = new ControllerHarness();
        _ = camera.Controller.TakePhoto();
        var first = camera.State.Notice!;
        _ = camera.Controller.TakePhoto();
        var second = camera.State.Notice!;

        camera.Controller.DismissNotice(first.Id);
        camera.State.Notice.ShouldBe(second);
        camera.Controller.DismissNotice(second.Id);
        camera.State.Notice.ShouldBeNull();
        camera.Controller.DismissNotice(99);
        camera.State.Notice.ShouldBeNull();
    }

    [Fact]
    public void The_arguments_are_checked_and_the_defaults_are_the_real_timings()
    {
        Should.Throw<ArgumentNullException>(() => new CameraController(null!));
        ControllerTiming.Default.ReconnectAttempts.ShouldBe(5);
        ControllerTiming.Default.Answer.ShouldBe(TimeSpan.FromSeconds(10));
        new CameraController(new FakeLink(ReferenceCamera.Fake())).State.ShouldBe(CameraState.Initial);
    }

    /// <summary>A control port that accepts the connection attempt and never completes it.</summary>
    private sealed class Unanswering : ICameraTransport
    {
        public bool IsConnected => false;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A control port with nothing listening.</summary>
    private sealed class Refusing : ICameraTransport
    {
        public bool IsConnected => false;

        public Task ConnectAsync(CancellationToken cancellationToken) =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Settings_show_their_choice_or_their_text_and_one_the_camera_will_not_read_shows_nothing()
    {
        await using var camera = new ControllerHarness();
        var connected = await camera.ConnectedAsync();
        var choice = connected.Settings.First(s => s.Value is not null);
        choice.Shown.ShouldBe(choice.Menu.LabelFor(choice.Value!.Value));
        connected.Settings.Single(s => s.Menu.Id == MenuIds.WifiName).Shown.ShouldBe(connected.CameraName);

        await using var refusing = new ControllerHarness();
        refusing.Control.ForcedRefusals[GpSockCommand.MenuGetParameter] = NakCode.InvalidCommand;
        var unread = await refusing.ConnectedAsync();

        unread.Settings.ShouldAllBe(s => s.Value == null && s.Text == null && s.Shown == null);
        unread.CameraName.ShouldBeNull();
    }

    [Fact]
    public async Task A_controller_nobody_is_watching_still_keeps_its_state()
    {
        await using var camera = new ControllerHarness();
        await using var unwatched = new CameraController(camera.Link);

        unwatched.SwitchMode(CaptureMode.Photo).ShouldBeNull();
        unwatched.State.Mode.ShouldBe(CaptureMode.Photo);

        _ = unwatched.Connect();
        await ControllerHarness.EventuallyAsync(() => unwatched.State.IsConnected);
        await unwatched.TakePhoto()!;

        unwatched.State.Notice!.Text.ShouldBe("Photo saved to the camera's card.");
    }
}
