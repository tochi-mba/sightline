using System.Collections.Immutable;
using Sightline.Core.Camera;

namespace Sightline.Core.Sentry;

/// <summary>
/// What Sentry does when the alarm goes up or comes down, which takes the PC: a notification, a
/// snapshot in a folder.
/// </summary>
public interface ISentryActions
{
    /// <summary>The alarm went up at <paramref name="at"/>; <paramref name="snapshot"/> is the picture that raised it.</summary>
    void AlarmRaised(DateTimeOffset at, byte[] snapshot, bool saveSnapshot);

    /// <summary>The alarm that went up came down.</summary>
    void AlarmEnded();
}

/// <summary>One alarm, for the Sentry page's list.</summary>
/// <param name="Number">Its number since Sentry was armed, from 1.</param>
/// <param name="At">When it went up.</param>
/// <param name="Snapshot">The picture that raised it.</param>
public sealed record SentryAlarm(int Number, DateTimeOffset At, byte[] Snapshot);

/// <summary>Sentry as the app runs it.</summary>
/// <param name="Armed">Whether Sentry has been armed, whatever it is doing now.</param>
/// <param name="Watch">Where the watch itself is: arming, watching, raising the alarm.</param>
/// <param name="Score">How much of the picture changed in the last frame looked at, for a live meter.</param>
/// <param name="Alarms">The alarms raised since it was armed, newest first, the latest few only.</param>
public sealed record SentryStatus(bool Armed, SentryState Watch, double Score, ImmutableList<SentryAlarm> Alarms)
{
    /// <summary>Not armed, and nothing seen.</summary>
    public static SentryStatus Off { get; } = new(false, SentryState.Disarmed, 0, []);
}

/// <summary>How Sentry is to run, read each time it is armed.</summary>
/// <param name="Watch">How it watches.</param>
/// <param name="SaveSnapshots">Whether the picture that raised an alarm is saved.</param>
/// <param name="Records">Whether an alarm starts the camera recording to its card until it ends.</param>
public sealed record SentryOptions(SentrySettings Watch, bool SaveSnapshots, bool Records)
{
    /// <summary>The default watch, saving snapshots and recording, as the Android app does.</summary>
    public static SentryOptions Default { get; } = new(SentrySettings.Default, true, true);
}

/// <summary>
/// Runs Sentry on the live view: arms the watch, feeds it frames, and does what an alarm calls for. The
/// Android app's runner, rule for rule.
/// </summary>
/// <remarks>
/// Armed, it holds the live view open so pictures keep coming with the window closed to the tray, and looks
/// at no more than five frames a second: enough to see a person cross the picture, few enough that decoding
/// them costs the PC little. When the alarm goes up it tells the actions, and if the options say so starts
/// the camera recording to its own card, unless the camera was recording already; when the alarm ends it
/// stops a recording it started, and only one it started.
/// </remarks>
public sealed class SentryRunner : IDisposable
{
    /// <summary>Who holds the live view open while Sentry is armed.</summary>
    private const string LiveHolder = "sentry";

    /// <summary>How many alarms the Sentry page lists.</summary>
    private const int KeptAlarms = 50;

    /// <summary>At most five frames a second are looked at.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(200);

    private readonly CameraController controller;
    private readonly Func<SentryOptions> options;
    private readonly ISentryActions actions;
    private readonly Func<byte[], LumaGrid?> sampler;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private SentryStatus status = SentryStatus.Off;
    private SentryWatch? armed;
    private SentryOptions running = SentryOptions.Default;
    private long? lastLooked;
    private bool recordingForAlarm;
    private int alarmNumber;

    /// <summary>Creates a disarmed runner.</summary>
    /// <param name="controller">The camera it watches through.</param>
    /// <param name="options">How to run, read at each arming.</param>
    /// <param name="actions">What an alarm does on this PC.</param>
    /// <param name="sampler">Turns a frame's JPEG into a brightness grid, or null when it cannot be decoded.</param>
    /// <param name="clock">The time; the system's when omitted.</param>
    public SentryRunner(
        CameraController controller,
        Func<SentryOptions> options,
        ISentryActions actions,
        Func<byte[], LumaGrid?> sampler,
        TimeProvider? clock = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        this.sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Sentry's state, for its page and the tray.</summary>
    public SentryStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    /// <summary>Raised after every change to <see cref="Status"/>, from whatever thread made it.</summary>
    public event Action<SentryStatus>? StatusChanged;

    /// <summary>Arms Sentry with the options as they are now. Arming again starts over.</summary>
    public void Arm()
    {
        Disarm();
        var now = options();
        var watch = new SentryWatch(now.Watch, clock);
        watch.Arm();
        SentryStatus next;
        lock (gate)
        {
            armed = watch;
            running = now;
            lastLooked = null;
            alarmNumber = 0;
            status = next = new SentryStatus(true, watch.State, 0, []);
        }

        StatusChanged?.Invoke(next);
        controller.FrameArrived += Look;
        // Arming is the person asking for the picture, for as long as Sentry stays armed: it watches nothing else.
        controller.HoldLive(LiveHolder, asking: true);
    }

    /// <summary>Stands Sentry down, stopping a recording it started. Harmless when not armed.</summary>
    public void Disarm()
    {
        controller.FrameArrived -= Look;
        controller.ReleaseLive(LiveHolder);
        bool stop;
        lock (gate)
        {
            armed = null;
            stop = recordingForAlarm;
            recordingForAlarm = false;
        }

        if (stop)
        {
            StopRecording();
        }

        Update(s => s with { Armed = false, Watch = SentryState.Disarmed, Score = 0 });
    }

    /// <inheritdoc />
    public void Dispose() => Disarm();

    private void Look(LiveFrame frame)
    {
        SentryEvent? happened;
        SentryAlarm? raised = null;
        SentryOptions now;
        bool stop = false;
        SentryStatus next;
        lock (gate)
        {
            if (armed is not { } watch || (lastLooked is { } last && clock.GetElapsedTime(last) < LookEvery))
            {
                return;
            }

            if (sampler(frame.Jpeg) is not { } grid)
            {
                return;
            }

            lastLooked = clock.GetTimestamp();
            now = running;
            happened = watch.Observe(grid);
            if (happened == SentryEvent.AlarmRaised)
            {
                raised = new SentryAlarm(++alarmNumber, clock.GetUtcNow(), frame.Jpeg);
                status = status with { Alarms = status.Alarms.Insert(0, raised).Take(KeptAlarms).ToImmutableList() };
            }
            else if (happened == SentryEvent.AlarmEnded)
            {
                stop = recordingForAlarm;
                recordingForAlarm = false;
            }

            status = next = status with { Watch = watch.State, Score = watch.LastScore };
        }

        StatusChanged?.Invoke(next);
        if (raised is not null)
        {
            actions.AlarmRaised(raised.At, raised.Snapshot, now.SaveSnapshots);
            StartRecording(now);
        }
        else if (happened == SentryEvent.AlarmEnded)
        {
            if (stop)
            {
                StopRecording();
            }

            actions.AlarmEnded();
        }
    }

    private void StartRecording(SentryOptions now)
    {
        if (!now.Records || controller.State.IsRecording)
        {
            return;
        }

        // Busy with something else the person asked for, the camera is left to it: this alarm then has a
        // snapshot and a notification, but no recording.
        var started = controller.ToggleRecording() is not null;
        lock (gate)
        {
            recordingForAlarm = started;
        }
    }

    private void StopRecording()
    {
        if (controller.State.IsRecording)
        {
            _ = controller.ToggleRecording();
        }
    }

    private void Update(Func<SentryStatus, SentryStatus> change)
    {
        SentryStatus next;
        lock (gate)
        {
            status = next = change(status);
        }

        StatusChanged?.Invoke(next);
    }
}
