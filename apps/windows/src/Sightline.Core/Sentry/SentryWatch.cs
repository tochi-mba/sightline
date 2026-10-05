namespace Sightline.Core.Sentry;

/// <summary>How Sentry watches; the same defaults as the Android app.</summary>
/// <param name="Sensitivity">How much change counts as motion.</param>
/// <param name="Zone">Which part of the picture is watched; all of it when null.</param>
/// <param name="ArmDelay">How long after arming before anything can raise the alarm.</param>
/// <param name="Sustain">How long motion must last to raise the alarm.</param>
/// <param name="Quiet">How long without motion before an alarm is over.</param>
/// <param name="Cooldown">After an alarm, how long before another can be raised.</param>
public sealed record SentrySettings(
    Sensitivity Sensitivity,
    Zone? Zone,
    TimeSpan ArmDelay,
    TimeSpan Sustain,
    TimeSpan Quiet,
    TimeSpan Cooldown)
{
    /// <summary>Medium sensitivity, the whole picture, ten seconds to leave, half a second of motion, five quiet, thirty between alarms.</summary>
    public static SentrySettings Default { get; } = new(
        Sensitivity.Medium, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

    /// <summary>Checks the times make sense.</summary>
    public SentrySettings Validated()
    {
        if (ArmDelay < TimeSpan.Zero || Sustain < TimeSpan.Zero || Cooldown < TimeSpan.Zero)
        {
            throw new ArgumentException("Sentry's times cannot be negative.");
        }

        return Quiet > TimeSpan.Zero ? this : throw new ArgumentException("An alarm needs a moment of quiet to end.");
    }
}

/// <summary>Where Sentry is.</summary>
public enum SentryState
{
    /// <summary>Not watching.</summary>
    Disarmed,

    /// <summary>Armed, waiting out the arm delay.</summary>
    Arming,

    /// <summary>Watching for motion.</summary>
    Watching,

    /// <summary>Motion was seen and is still going on.</summary>
    Alarm,

    /// <summary>An alarm just ended; another cannot be raised until the cooldown is over.</summary>
    Cooldown,
}

/// <summary>What one frame did: the alarm going up or coming down.</summary>
public enum SentryEvent
{
    /// <summary>Motion lasted long enough.</summary>
    AlarmRaised,

    /// <summary>The motion stopped.</summary>
    AlarmEnded,
}

/// <summary>
/// Sentry's watch over the live view: arming, watching, raising the alarm and standing down. Pure: it is
/// handed brightness grids and reads the time from <see cref="TimeProvider"/>, so a night's watch is tested
/// in milliseconds. The Android app's watch, state for state.
/// </summary>
public sealed class SentryWatch
{
    private readonly SentrySettings settings;
    private readonly TimeProvider clock;
    private readonly MotionDetector detector;
    private long since;
    private long? motionSince;
    private long lastMotion;

    /// <summary>Creates a disarmed watch.</summary>
    public SentryWatch(SentrySettings settings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings.Validated();
        this.clock = clock ?? TimeProvider.System;
        detector = new MotionDetector(settings.Sensitivity, settings.Zone);
        since = this.clock.GetTimestamp();
        lastMotion = since;
    }

    /// <summary>Where Sentry is now.</summary>
    public SentryState State { get; private set; } = SentryState.Disarmed;

    /// <summary>The most of the picture that has changed at once during the current alarm.</summary>
    public double Peak { get; private set; }

    /// <summary>The share of the picture that changed in the last frame.</summary>
    public double LastScore { get; private set; }

    /// <summary>Starts watching, after the arm delay. Arming again restarts the delay.</summary>
    public void Arm()
    {
        detector.Reset();
        Enter(settings.ArmDelay > TimeSpan.Zero ? SentryState.Arming : SentryState.Watching);
    }

    /// <summary>Stops watching; an alarm in progress simply ends.</summary>
    public void Disarm() => Enter(SentryState.Disarmed);

    /// <summary>Looks at one frame's brightness.</summary>
    /// <returns>The alarm going up or coming down on this frame, otherwise null.</returns>
    public SentryEvent? Observe(LumaGrid grid)
    {
        var now = State;
        if (now == SentryState.Disarmed)
        {
            return null;
        }

        var score = detector.Observe(grid);
        LastScore = score;
        var moving = detector.IsMotion(score);
        if (moving)
        {
            lastMotion = clock.GetTimestamp();
            motionSince ??= lastMotion;
        }
        else
        {
            motionSince = null;
        }

        switch (now)
        {
            case SentryState.Arming:
                if (Elapsed(since) >= settings.ArmDelay)
                {
                    Enter(SentryState.Watching);
                }

                return null;
            case SentryState.Watching:
                return RaiseIfSustained(score);
            case SentryState.Alarm:
                return ContinueAlarm(moving, score);
            default:
                // Cooling down, the only state left.
                if (Elapsed(since) < settings.Cooldown)
                {
                    return null;
                }

                Enter(SentryState.Watching);
                return RaiseIfSustained(score);
        }
    }

    private SentryEvent? ContinueAlarm(bool moving, double score)
    {
        if (moving)
        {
            Peak = Math.Max(Peak, score);
            return null;
        }

        if (Elapsed(lastMotion) < settings.Quiet)
        {
            return null;
        }

        Enter(settings.Cooldown > TimeSpan.Zero ? SentryState.Cooldown : SentryState.Watching);
        return SentryEvent.AlarmEnded;
    }

    private SentryEvent? RaiseIfSustained(double score)
    {
        if (motionSince is not { } started || Elapsed(started) < settings.Sustain)
        {
            return null;
        }

        State = SentryState.Alarm;
        Peak = score;
        return SentryEvent.AlarmRaised;
    }

    private TimeSpan Elapsed(long from) => clock.GetElapsedTime(from);

    private void Enter(SentryState next)
    {
        State = next;
        since = clock.GetTimestamp();
    }
}
