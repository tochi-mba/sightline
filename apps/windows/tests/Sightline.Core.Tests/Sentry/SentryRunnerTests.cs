using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Sentry;
using Sightline.Core.Tests.Camera;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Sentry;

/// <summary>Sentry run on a real controller's live view, over the fake camera.</summary>
public sealed class SentryRunnerTests
{
    private static readonly LumaGrid Still = new(10, 10, Enumerable.Repeat(100, 100).ToArray());

    private readonly ManualClock clock = new();
    private readonly RecordedActions actions = new();
    private SentryOptions options = new(
        SentrySettings.Default with { ArmDelay = TimeSpan.Zero, Sustain = TimeSpan.Zero, Quiet = TimeSpan.FromSeconds(1), Cooldown = TimeSpan.Zero },
        SaveSnapshots: true,
        Records: true);

    private volatile bool moving;
    private volatile bool undecodable;
    private int looked;
    private int movingFrame;

    /// <summary>A connected camera streaming steadily, each picture moving the clock on a quarter of a second before Sentry sees it.</summary>
    private async Task<ControllerHarness> ConnectedAsync()
    {
        var camera = new ControllerHarness
        {
            Stream = s =>
            {
                s.Frames.AddRange(Enumerable.Range(0, 200).Select(_ => FakeRtspCamera.Jpeg(600)));
                s.Pace = TimeSpan.FromMilliseconds(5);
            },
        };
        camera.Controller.FrameArrived += _ => clock.Advance(TimeSpan.FromMilliseconds(250));
        await camera.ConnectedAsync();
        return camera;
    }

    private SentryRunner Runner(ControllerHarness camera) => new(camera.Controller, () => options, actions, Sample, clock);

    private LumaGrid? Sample(byte[] jpeg)
    {
        Interlocked.Increment(ref looked);
        if (undecodable)
        {
            return null;
        }

        if (!moving)
        {
            return Still;
        }

        var at = Interlocked.Increment(ref movingFrame) % 2 * 5;
        return new LumaGrid(10, 10, Enumerable.Range(0, 100).Select(i => i % 10 >= at && i % 10 < at + 4 && i / 10 < 4 ? 250 : 100).ToArray());
    }

    [Fact]
    public async Task An_alarm_tells_the_pc_records_while_it_lasts_and_stops_what_it_started()
    {
        await using var camera = await ConnectedAsync();
        using var runner = Runner(camera);
        var seen = new List<SentryStatus>();
        runner.StatusChanged += status =>
        {
            lock (seen)
            {
                seen.Add(status);
            }
        };

        runner.Arm();
        runner.Status.Armed.ShouldBeTrue();
        await camera.UntilAsync(s => s.Live is LiveView.Playing);
        await ControllerHarness.EventuallyAsync(() => runner.Status.Watch == SentryState.Watching && Volatile.Read(ref looked) > 2);

        moving = true;
        await ControllerHarness.EventuallyAsync(() => actions.Raised.Count == 1);
        await camera.UntilAsync(s => s.IsRecording);
        runner.Status.Alarms.Single().Number.ShouldBe(1);
        actions.Raised.Single().Save.ShouldBeTrue();

        moving = false;
        await ControllerHarness.EventuallyAsync(() => actions.Ended == 1);
        await camera.UntilAsync(s => !s.IsRecording);

        runner.Disarm();
        runner.Status.ShouldBe(runner.Status with { Armed = false, Watch = SentryState.Disarmed, Score = 0 });
        await camera.UntilAsync(s => s.Live is LiveView.Off);
        lock (seen)
        {
            seen.ShouldContain(s => s.Watch == SentryState.Alarm);
        }
    }

    [Fact]
    public async Task A_recording_the_camera_was_already_making_is_left_alone()
    {
        await using var camera = await ConnectedAsync();
        await camera.Controller.ToggleRecording()!;
        using var runner = Runner(camera);
        runner.Arm();
        await ControllerHarness.EventuallyAsync(() => runner.Status.Watch == SentryState.Watching && Volatile.Read(ref looked) > 2);

        moving = true;
        await ControllerHarness.EventuallyAsync(() => actions.Raised.Count == 1);
        moving = false;
        await ControllerHarness.EventuallyAsync(() => actions.Ended == 1);
        runner.Disarm();

        camera.State.IsRecording.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_recording_asked_for_an_alarm_only_tells_and_disarming_mid_alarm_stops_the_recording()
    {
        await using var camera = await ConnectedAsync();
        options = options with { Records = false, SaveSnapshots = false };
        using (var quiet = Runner(camera))
        {
            quiet.Arm();
            await ControllerHarness.EventuallyAsync(() => quiet.Status.Watch == SentryState.Watching && Volatile.Read(ref looked) > 2);
            moving = true;
            await ControllerHarness.EventuallyAsync(() => actions.Raised.Count == 1);
            actions.Raised.Single().Save.ShouldBeFalse();
            camera.State.IsRecording.ShouldBeFalse();
        }

        moving = false;
        options = options with { Records = true };
        using var runner = Runner(camera);
        runner.Arm();
        await ControllerHarness.EventuallyAsync(() => runner.Status.Watch == SentryState.Watching && Volatile.Read(ref looked) > 6);
        moving = true;
        await camera.UntilAsync(s => s.IsRecording);

        runner.Disarm();

        await camera.UntilAsync(s => !s.IsRecording);
    }

    [Fact]
    public async Task No_more_than_five_pictures_a_second_are_looked_at_and_an_undecodable_one_is_skipped()
    {
        await using var camera = await ConnectedAsync();
        camera.Controller.FrameArrived += _ => clock.Advance(TimeSpan.FromMilliseconds(-200));
        using var runner = Runner(camera);
        var pictures = 0;
        camera.Controller.FrameArrived += _ => Interlocked.Increment(ref pictures);
        runner.Arm();

        await ControllerHarness.EventuallyAsync(() => Volatile.Read(ref pictures) >= 40);
        // A twentieth of a second a picture: one picture in four is looked at.
        Volatile.Read(ref looked).ShouldBeLessThan(Volatile.Read(ref pictures) / 2);

        undecodable = true;
        var before = Volatile.Read(ref looked);
        await ControllerHarness.EventuallyAsync(() => Volatile.Read(ref looked) > before + 5);
        runner.Status.Watch.ShouldBe(SentryState.Watching);
    }

    [Fact]
    public async Task Disarming_when_not_armed_is_harmless_and_arming_again_starts_over()
    {
        await using var camera = await ConnectedAsync();
        using var runner = Runner(camera);
        runner.Disarm();
        runner.Status.ShouldBe(SentryStatus.Off);

        runner.Arm();
        await ControllerHarness.EventuallyAsync(() => Volatile.Read(ref looked) > 2);
        moving = true;
        await ControllerHarness.EventuallyAsync(() => actions.Raised.Count == 1);

        runner.Arm();

        runner.Status.Alarms.ShouldBeEmpty();
        runner.Status.Armed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_picture_already_on_its_way_when_sentry_stands_down_is_ignored()
    {
        await using var camera = await ConnectedAsync();
        using var runner = Runner(camera);
        var disarmed = 0;
        // Handed each picture before Sentry is, as a picture in flight during a disarm would be.
        camera.Controller.FrameArrived += _ =>
        {
            if (Volatile.Read(ref looked) > 2 && Interlocked.Exchange(ref disarmed, 1) == 0)
            {
                runner.Disarm();
            }
        };
        runner.Arm();

        await ControllerHarness.EventuallyAsync(() => Volatile.Read(ref disarmed) == 1);
        var after = Volatile.Read(ref looked);
        await camera.UntilAsync(s => s.Live is LiveView.Off);

        Volatile.Read(ref looked).ShouldBe(after);
        runner.Status.Armed.ShouldBeFalse();
    }

    [Fact]
    public async Task Its_parts_are_required()
    {
        await using var camera = new ControllerHarness();
        Should.Throw<ArgumentNullException>(() => new SentryRunner(null!, () => options, actions, Sample));
        Should.Throw<ArgumentNullException>(() => new SentryRunner(camera.Controller, null!, actions, Sample));
        Should.Throw<ArgumentNullException>(() => new SentryRunner(camera.Controller, () => options, null!, Sample));
        Should.Throw<ArgumentNullException>(() => new SentryRunner(camera.Controller, () => options, actions, null!));
        using var runner = new SentryRunner(camera.Controller, () => options, actions, Sample);
        runner.Status.ShouldBe(SentryStatus.Off);
        SentryOptions.Default.Records.ShouldBeTrue();
    }

    private sealed class RecordedActions : ISentryActions
    {
        private int ended;

        public List<(DateTimeOffset At, bool Save)> Raised { get; } = [];

        public int Ended => Volatile.Read(ref ended);

        public void AlarmRaised(DateTimeOffset at, byte[] snapshot, bool saveSnapshot)
        {
            lock (Raised)
            {
                Raised.Add((at, saveSnapshot));
            }
        }

        public void AlarmEnded() => Interlocked.Increment(ref ended);
    }
}
