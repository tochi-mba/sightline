using Shouldly;
using Sightline.Core.Camera;
using Sightline.Protocol.GpSock;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>The live view, the shutter, the capture mode and the camera's settings.</summary>
public sealed class OperationsTests
{
    [Fact]
    public async Task The_live_view_shows_while_somebody_holds_it_and_the_stream_stays_open_when_nobody_does()
    {
        await using var camera = new ControllerHarness();
        camera.Stream = s =>
        {
            s.Pace = TimeSpan.FromMilliseconds(20);
            s.Frames.AddRange(Enumerable.Range(0, 300).Select(i => FakeRtspCamera.Jpeg(600 + i)));
        };
        var frames = new List<LiveFrame>();
        camera.Controller.FrameArrived += frames.Add;
        await camera.ConnectedAsync();

        camera.Controller.HoldLive("window");
        camera.Controller.HoldLive("sentry");
        (await camera.UntilAsync(s => s.Live is LiveView.Playing)).HoldsLivePicture.ShouldBeTrue();
        camera.Controller.ReleaseLive("window");
        // Still running for Sentry: playing, or between quick stalls on these shortened timings.
        camera.State.Live.ShouldNotBeOfType<LiveView.Off>();
        camera.Controller.ReleaseLive("sentry");
        await camera.UntilAsync(s => s.Live is LiveView.Off);

        // The camera answers one stream per power-on, so letting go of the picture keeps the stream.
        camera.Streams[0].IsConnected.ShouldBeTrue();
        camera.State.HoldsLivePicture.ShouldBeTrue();
        camera.Controller.HoldLive("window");
        await camera.UntilAsync(s => s.Live is LiveView.Playing);
        camera.Streams.Count.ShouldBe(1);
        frames[0].Width.ShouldBe(640);
    }

    [Fact]
    public async Task Holding_the_live_view_before_connecting_starts_it_once_connected()
    {
        await using var camera = new ControllerHarness();
        camera.Controller.HoldLive("window");
        camera.State.Live.ShouldBeOfType<LiveView.Off>();

        await camera.ConnectedAsync();

        (await camera.UntilAsync(s => s.Live is LiveView.Playing)).Live.ShouldBeOfType<LiveView.Playing>();
    }

    [Fact]
    public async Task A_stream_the_camera_ends_has_its_rate_measured_and_is_not_asked_for_again()
    {
        await using var camera = new ControllerHarness();
        camera.Stream = s =>
        {
            s.Pace = TimeSpan.FromMilliseconds(20);
            s.Frames.AddRange(Enumerable.Range(0, 80).Select(i => FakeRtspCamera.Jpeg(300 + i)));
            s.ClosesAfterFrames = true;
        };
        await camera.ConnectedAsync();

        camera.Controller.HoldLive("window");
        var gone = (await camera.UntilAsync(s => s.Live is LiveView.Unavailable)).Live.ShouldBeOfType<LiveView.Unavailable>();

        gone.Reason.ShouldBe("The camera ended its live picture. " + LivePictureUnavailableException.Advice);
        camera.Seen.ShouldContain(s => s.Live is LiveView.Playing && ((LiveView.Playing)s.Live).FramesPerSecond > 0);
        camera.State.HoldsLivePicture.ShouldBeFalse();
        camera.Controller.ReleaseLive("window");
        camera.Controller.HoldLive("window");
        await camera.UntilAsync(s => s.Live is LiveView.Unavailable);
        camera.Streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_quiet_stream_is_said_and_waited_on_without_another_connection()
    {
        await using var camera = new ControllerHarness();
        camera.Stream = s => s.StreamStarted = () => false;
        await camera.ConnectedAsync();

        camera.Controller.HoldLive("window");

        var quiet = (await camera.UntilAsync(s => s.Live is LiveView.Interrupted)).Live.ShouldBeOfType<LiveView.Interrupted>();
        quiet.Reason.ShouldBe("The camera sent nothing for 0 seconds.");
        await Task.Delay(800);
        camera.Streams.Count.ShouldBe(1);
        camera.Streams[0].Verbs.ShouldBe(["DESCRIBE", "SETUP", "PLAY"]);
    }

    [Fact]
    public async Task A_stream_the_camera_refuses_says_why_and_is_not_asked_for_again()
    {
        await using var camera = new ControllerHarness();
        camera.Stream = s => s.SetupStatus = 454;
        await camera.ConnectedAsync();

        camera.Controller.HoldLive("window");

        var gone = (await camera.UntilAsync(s => s.Live is LiveView.Unavailable)).Live.ShouldBeOfType<LiveView.Unavailable>();
        gone.Reason.ShouldStartWith("The camera refused the video track (454).");
        await Task.Delay(300);
        camera.Streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Switching_the_camera_off_and_on_brings_the_picture_back_with_the_reconnect()
    {
        await using var camera = new ControllerHarness();
        camera.Stream = s =>
        {
            s.Pace = TimeSpan.FromMilliseconds(20);
            s.Frames.AddRange(Enumerable.Range(0, 300).Select(i => FakeRtspCamera.Jpeg(300 + i)));
        };
        await camera.ConnectedAsync();
        camera.Controller.HoldLive("window");
        await camera.UntilAsync(s => s.Live is LiveView.Playing);
        camera.Streams[0].HangUp();
        await camera.UntilAsync(s => s.Live is LiveView.Unavailable);

        // Switched off: the network goes. Switched on: the reconnect finds a camera with its picture to give.
        camera.Link.PowerCycle();
        camera.Link.Lease.Lose();

        await camera.UntilAsync(s => s.Live is LiveView.Playing && s.HoldsLivePicture);
        camera.Streams.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_shutter_records_and_stops_and_says_what_the_camera_reports()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();

        await camera.Controller.Shutter()!;
        camera.Control.IsRecording.ShouldBeTrue();
        camera.State.Notice!.Text.ShouldBe("Recording to the camera's card.");
        await camera.Controller.Shutter()!;

        camera.Control.IsRecording.ShouldBeFalse();
        camera.State.Notice!.Text.ShouldBe("Recording stopped and saved to the card.");
        camera.State.Task.ShouldBeNull();
        camera.Seen.ShouldContain(s => s.Task == CameraTask.StartingRecording);
    }

    [Fact]
    public async Task A_camera_that_ignores_the_record_button_is_reported()
    {
        await using var camera = new ControllerHarness();
        camera.Control.ForcedAnswers[GpSockCommand.RecordToggle] = [];
        await camera.ConnectedAsync();

        await camera.Controller.ToggleRecording()!;
        camera.State.Notice!.Text.ShouldBe("The camera did not start recording.");

        camera.Control.IsRecording = true;
        await camera.UntilAsync(s => s.IsRecording);
        await camera.Controller.ToggleRecording()!;
        camera.State.Notice!.Text.ShouldBe("The camera is still recording.");
    }

    [Fact]
    public async Task Photos_are_taken_in_capture_mode_and_refused_while_recording()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();

        await camera.Controller.SwitchMode(CaptureMode.Photo)!;
        await camera.Controller.Shutter()!;
        camera.Control.PicturesTaken.ShouldBe(1);
        camera.Control.Mode.ShouldBe(CameraMode.Capture);
        camera.State.Notice!.Text.ShouldBe("Photo saved to the camera's card.");

        await camera.Controller.ToggleRecording()!;
        camera.State.Mode.ShouldBe(CaptureMode.Video);
        await camera.Controller.TakePhoto()!;
        camera.State.Notice!.Text.ShouldBe("Stop recording to take a photo.");
        await camera.Controller.SwitchMode(CaptureMode.Photo)!;
        camera.State.Notice!.Text.ShouldBe("Stop recording to switch to photos.");
        camera.Controller.SwitchMode(CaptureMode.Video).ShouldBeNull();
    }

    [Fact]
    public async Task Choosing_a_mode_before_connecting_only_chooses_the_shutter()
    {
        await using var camera = new ControllerHarness();

        camera.Controller.SwitchMode(CaptureMode.Photo).ShouldBeNull();

        camera.State.Mode.ShouldBe(CaptureMode.Photo);
    }

    [Fact]
    public async Task A_refusal_is_explained_and_a_second_press_ignored()
    {
        await using var camera = new ControllerHarness();
        camera.Control.ForcedRefusals[GpSockCommand.CapturePicture] = NakCode.FullStorage;
        await camera.ConnectedAsync();
        // A slow camera from here, so the first press is still running when the second comes.
        camera.Control.AnswerDelay = TimeSpan.FromMilliseconds(20);

        var first = camera.Controller.TakePhoto();
        camera.Controller.TakePhoto().ShouldBeNull();
        await first!;

        camera.State.Notice!.Text.ShouldBe("The camera said no: the card is full.");
    }

    [Fact]
    public async Task An_operation_that_loses_the_camera_starts_a_reconnect()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();
        camera.Control.HangsUp = true;

        await camera.Controller.TakePhoto()!;

        (await camera.UntilAsync(s => s.Connection is Connection.Reconnecting)).Connection
            .ShouldBeOfType<Connection.Reconnecting>().Problem.Kind.ShouldBe(ProblemKind.Lost);
    }

    [Fact]
    public async Task A_setting_is_written_read_back_and_one_the_camera_does_not_keep_is_reported()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();

        await camera.Controller.ChangeSetting(MenuIds.RecordResolution, 1)!;
        camera.State.Settings.Single(s => s.Menu.Id == MenuIds.RecordResolution).Value.ShouldBe(1);
        camera.Control.IgnoredSettings.Add(MenuIds.RecordExposure);
        await camera.Controller.ChangeSetting(MenuIds.RecordExposure, 1)!;

        camera.State.Notice!.Text.ShouldStartWith("The camera kept Exposure at");
        camera.Control.SettingsWritten.ShouldBe([(MenuIds.RecordResolution, 1), (MenuIds.RecordExposure, 1)]);
    }

    [Fact]
    public async Task A_setting_the_camera_does_not_offer_or_one_while_recording_is_refused()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();

        await camera.Controller.ChangeSetting(0x7777, 0)!;
        camera.State.Notice!.Text.ShouldBe("The camera does not offer that setting.");
        await camera.Controller.ChangeSetting(MenuIds.WifiPassword, 0)!;
        await camera.Controller.ChangeSetting(MenuIds.RecordResolution, 200)!;
        await camera.Controller.ToggleRecording()!;
        await camera.Controller.ChangeSetting(MenuIds.RecordResolution, 1)!;

        camera.State.Notice!.Text.ShouldBe("Settings cannot change while the camera records.");
        camera.Control.SettingsWritten.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_picture_rate_is_counted_over_each_second_once_pictures_arrive()
    {
        await using var camera = new ControllerHarness
        {
            Stream = s =>
            {
                s.Frames.AddRange(Enumerable.Range(0, 40).Select(_ => FakeRtspCamera.Jpeg(600)));
                s.Pace = TimeSpan.FromMilliseconds(40);
            },
        };
        await camera.ConnectedAsync();

        camera.Controller.HoldLive("window");

        var playing = (await camera.UntilAsync(s => s.Live is LiveView.Playing { FramesPerSecond: > 0 })).Live;
        ((LiveView.Playing)playing).FramesPerSecond.ShouldBeInRange(5, 30);
    }

    [Fact]
    public async Task Letting_the_live_view_go_while_a_picture_is_handed_out_leaves_it_off()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();
        var pictures = 0;
        // Let go of from the stream's own thread, just before that run would say it is playing.
        camera.Controller.FrameArrived += _ =>
        {
            Interlocked.Increment(ref pictures);
            camera.Controller.ReleaseLive("window");
        };

        camera.Controller.HoldLive("window");
        await ControllerHarness.EventuallyAsync(() => Volatile.Read(ref pictures) > 0);
        await Task.Delay(200);

        camera.State.Live.ShouldBe(LiveView.Off.Instance);
    }

    [Fact]
    public async Task An_operation_cut_off_by_disconnecting_ends_without_a_word()
    {
        await using var camera = new ControllerHarness();
        await camera.ConnectedAsync();
        camera.Control.GoesQuiet = true;

        var photo = camera.Controller.TakePhoto()!;
        await camera.Controller.DisconnectAsync();
        await photo;

        camera.State.Notice.ShouldBeNull();
        camera.State.Task.ShouldBeNull();
        camera.State.Connection.ShouldBe(Connection.Idle.Instance);
    }
}
