using Shouldly;
using Sightline.Core.Sentry;
using Xunit;

namespace Sightline.Core.Tests.Sentry;

/// <summary>The detector and the watch, held to the Android app's cases.</summary>
public sealed class SentryTests
{
    private readonly ManualClock clock = new();
    private int movingFrame;

    private static LumaGrid Scene(int brightness, int square = 0, int squareBrightness = 0, int at = 0) => new(
        10,
        10,
        Enumerable.Range(0, 100).Select(i =>
        {
            int x = i % 10, y = i / 10;
            return x >= at && x < at + square && y >= at && y < at + square ? squareBrightness : brightness;
        }).ToArray());

    private static readonly LumaGrid Still = Scene(100);

    private LumaGrid Moving()
    {
        var at = movingFrame++ % 2 * 5;
        return new LumaGrid(10, 10, Enumerable.Range(0, 100).Select(i => i % 10 >= at && i % 10 < at + 4 && i / 10 < 4 ? 250 : 100).ToArray());
    }

    private List<SentryEvent> Feed(SentryWatch watch, double seconds, Func<LumaGrid> frame)
    {
        var events = new List<SentryEvent>();
        for (var fed = 0.0; fed < seconds - 1e-9; fed += 0.2)
        {
            if (watch.Observe(frame()) is { } happened)
            {
                events.Add(happened);
            }

            clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        return events;
    }

    private SentryWatch Watching(TimeSpan? cooldown = null)
    {
        var watch = new SentryWatch(SentrySettings.Default with { ArmDelay = TimeSpan.Zero, Cooldown = cooldown ?? TimeSpan.FromSeconds(30) }, clock);
        watch.Arm();
        watch.Observe(Still);
        return watch;
    }

    [Fact]
    public void Something_appearing_is_motion_and_a_whole_picture_brightening_is_not()
    {
        var detector = new MotionDetector(Sensitivity.Medium);
        detector.Observe(Scene(100)).ShouldBe(0);

        var score = detector.Observe(Scene(100, square: 3, squareBrightness: 200));
        score.ShouldBe(0.09, 1e-9);
        detector.IsMotion(score).ShouldBeTrue();

        var exposure = new MotionDetector(Sensitivity.High);
        exposure.Observe(Scene(100));
        exposure.Observe(Scene(160)).ShouldBe(0);
    }

    [Fact]
    public void A_change_that_stays_is_learnt_and_each_sensitivity_needs_its_own_amount()
    {
        var detector = new MotionDetector(Sensitivity.Medium);
        detector.Observe(Scene(100));
        for (var i = 0; i < 101; i++)
        {
            detector.Observe(Scene(100, 3, 200));
        }

        detector.IsMotion(detector.Observe(Scene(100, 3, 200))).ShouldBeFalse();

        bool MotionAt(Sensitivity sensitivity, int square, int change)
        {
            var d = new MotionDetector(sensitivity);
            d.Observe(Scene(100));
            return d.IsMotion(d.Observe(Scene(100, square, 100 + change)));
        }

        MotionAt(Sensitivity.High, 1, 50).ShouldBeTrue();
        MotionAt(Sensitivity.Medium, 1, 50).ShouldBeFalse();
        MotionAt(Sensitivity.Medium, 4, 40).ShouldBeTrue();
        MotionAt(Sensitivity.Low, 4, 40).ShouldBeFalse();
        MotionAt(Sensitivity.Low, 4, 60).ShouldBeTrue();
    }

    [Fact]
    public void Motion_outside_the_zone_is_ignored_and_bad_grids_and_zones_are_refused()
    {
        var doorway = Zone.Rectangle(10, 10, 0.5, 0.5, 1.0, 1.0);
        var detector = new MotionDetector(Sensitivity.Medium, doorway);
        detector.Observe(Scene(100));

        detector.Observe(Scene(100, 4, 220)).ShouldBe(0);
        detector.Observe(Scene(100, 2, 220, at: 6)).ShouldBe(4.0 / 25, 1e-9);
        doorway.Size.ShouldBe(25);
        Zone.Rectangle(10, 10, 0.25, 0.25, 0.75, 0.75).Size.ShouldBe(36);
        Zone.All(4, 4).Size.ShouldBe(16);
        detector.Observe(new LumaGrid(10, 10, new int[100])).ShouldBeGreaterThanOrEqualTo(0);
        new MotionDetector(Sensitivity.High).Observe(new LumaGrid(2, 2, new int[4])).ShouldBe(0);

        Should.Throw<ArgumentException>(() => new MotionDetector(Sensitivity.Medium, Zone.All(4, 4)).Observe(Scene(1)));
        Should.Throw<ArgumentException>(() => new LumaGrid(0, 1, []));
        Should.Throw<ArgumentException>(() => new LumaGrid(2, 2, new int[3]));
        Should.Throw<ArgumentException>(() => new LumaGrid(1, 1, [256]));
        Should.Throw<ArgumentException>(() => new Zone(2, 2, new bool[3]));
        Should.Throw<ArgumentException>(() => new Zone(2, 2, new bool[4]));
        Should.Throw<ArgumentException>(() => Zone.Rectangle(4, 4, 0.5, 0, 0.5, 1));
        Should.Throw<ArgumentException>(() => Zone.Rectangle(4, 4, -0.1, 0, 1, 1));
    }

    [Fact]
    public void Nothing_raises_the_alarm_while_disarmed_or_arming()
    {
        var watch = new SentryWatch(SentrySettings.Default, clock);
        Feed(watch, 2, Moving).ShouldBeEmpty();
        watch.State.ShouldBe(SentryState.Disarmed);

        watch.Arm();
        Feed(watch, 10, Moving).ShouldBeEmpty();
        watch.State.ShouldBe(SentryState.Arming);
        watch.Observe(Still);
        watch.State.ShouldBe(SentryState.Watching);
    }

    [Fact]
    public void Lasting_motion_raises_the_alarm_once_a_flicker_does_not_and_the_peak_is_kept()
    {
        var flicker = Watching();
        flicker.Observe(Moving());
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Feed(flicker, 3, () => Still).ShouldBeEmpty();

        var watch = Watching();
        Feed(watch, 2, Moving).ShouldBe([SentryEvent.AlarmRaised]);
        watch.State.ShouldBe(SentryState.Alarm);
        var peak = watch.Peak;
        watch.Observe(new LumaGrid(10, 10, Enumerable.Range(0, 100).Select(i => i < 60 ? 250 : 100).ToArray()));
        watch.Peak.ShouldBeGreaterThan(peak);
    }

    [Fact]
    public void An_alarm_ends_after_five_quiet_seconds_and_cools_down_before_another()
    {
        var watch = Watching();
        Feed(watch, 1, Moving);

        Feed(watch, 4, () => Still).ShouldBeEmpty();
        Feed(watch, 1, () => Still).ShouldBe([SentryEvent.AlarmEnded]);
        watch.State.ShouldBe(SentryState.Cooldown);
        Feed(watch, 29, Moving).ShouldBeEmpty();
        Feed(watch, 1, Moving).ShouldBe([SentryEvent.AlarmRaised]);
    }

    [Fact]
    public void A_quiet_cooldown_returns_to_watching_and_no_cooldown_goes_straight_back()
    {
        var watch = Watching();
        Feed(watch, 1, Moving);
        Feed(watch, 6, () => Still);
        Feed(watch, 31, () => Still);
        watch.State.ShouldBe(SentryState.Watching);

        var eager = Watching(TimeSpan.Zero);
        Feed(eager, 1, Moving);
        Feed(eager, 6, () => Still);
        eager.State.ShouldBe(SentryState.Watching);
    }

    [Fact]
    public void Disarming_ends_an_alarm_and_times_that_make_no_sense_are_refused()
    {
        var watch = Watching();
        Feed(watch, 1, Moving);
        watch.Disarm();
        watch.State.ShouldBe(SentryState.Disarmed);
        watch.Observe(Still).ShouldBeNull();
        watch.LastScore.ShouldBeGreaterThan(0);

        Should.Throw<ArgumentException>(() => new SentryWatch(SentrySettings.Default with { ArmDelay = TimeSpan.FromSeconds(-1) }));
        Should.Throw<ArgumentException>(() => new SentryWatch(SentrySettings.Default with { Quiet = TimeSpan.Zero }));
        new SentryWatch(SentrySettings.Default).State.ShouldBe(SentryState.Disarmed);
    }

    /// <summary>A clock moved by the test.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }
}
