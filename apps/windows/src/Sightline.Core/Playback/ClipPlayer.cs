namespace Sightline.Core.Playback;

/// <summary>What a player is doing.</summary>
public enum PlayerPhase
{
    /// <summary>Waiting for enough of the clip to play through without stopping.</summary>
    Waiting,

    /// <summary>Playing.</summary>
    Playing,

    /// <summary>Stopped where it was, because the person said so.</summary>
    Paused,

    /// <summary>Played to the end.</summary>
    Ended,
}

/// <summary>What one step of a player says to do.</summary>
/// <param name="Picture">A picture to show now, or null to keep showing the one shown last.</param>
/// <param name="Sound">Sound to queue behind whatever is already queued.</param>
/// <param name="SoundRestarts">Whether to drop the sound already queued first: the clip paused, waited or jumped.</param>
public readonly record struct PlayerStep(TimedPicture? Picture, IReadOnlyList<SoundSlice> Sound, bool SoundRestarts);

/// <summary>
/// Plays a clip that is still coming off the card: which picture shows when, which sound to queue, and when to
/// wait for more of the clip rather than stop part-way.
/// </summary>
/// <remarks>
/// <para>
/// The card is slower than the clip: a 1080p clip comes off it at a third to two-thirds of the speed it plays
/// at (measured 2026-10-07). So playing starts only once the rest will arrive a little before it is needed;
/// until then the first picture shows and the wait is said. That is judged in bytes, from how fast they have
/// been coming and how many the clip has: they come steadily, where the clip is ready only a whole run of sound
/// at a time, which would make a judgement in seconds of clip hopeful just after each run. If the download falls
/// behind all the same, the player waits rather than skips.
/// </para>
/// <para>
/// It does nothing by itself: whatever shows the clip calls <see cref="Step"/> on a timer, and the clock is
/// handed in, so it is tested on time a test controls.
/// </para>
/// </remarks>
public sealed class ClipPlayer
{
    // How much of the clip must be ready ahead of where it plays before playing starts, and how long before it is
    // needed the last of it must be judged to arrive.
    private static readonly TimeSpan DefaultLead = TimeSpan.FromSeconds(1);

    // How far back the rate the clip arrives at is judged over, and how much of that is needed to judge it at all.
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RateNeeds = TimeSpan.FromSeconds(1);

    private readonly ClipReader clip;
    private readonly long length;
    private readonly Func<TimeSpan> now;
    private readonly TimeSpan lead;
    private readonly Queue<(TimeSpan Wall, long Bytes)> arrivals = new();
    private TimeSpan position;
    private TimeSpan startedAt;
    private TimeSpan startedFrom;
    private int soundRunsQueued;
    private bool soundRestarts = true;
    private bool wanted = true;
    private int shown = -1;

    /// <summary>A player for <paramref name="clip"/>, on the clock <paramref name="now"/>.</summary>
    /// <param name="clip">The clip, arriving.</param>
    /// <param name="length">How many bytes the whole clip has, as the card lists it.</param>
    /// <param name="now">The time on a clock that only goes forward.</param>
    /// <param name="lead">
    /// How much must be ready ahead before playing starts, and how long before it is needed the last of the clip
    /// must be due; a second when omitted.
    /// </param>
    public ClipPlayer(ClipReader clip, long length, Func<TimeSpan> now, TimeSpan? lead = null)
    {
        this.clip = clip ?? throw new ArgumentNullException(nameof(clip));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        this.length = length;
        this.now = now ?? throw new ArgumentNullException(nameof(now));
        this.lead = lead ?? DefaultLead;
    }

    /// <summary>What the player is doing.</summary>
    public PlayerPhase Phase { get; private set; } = PlayerPhase.Waiting;

    /// <summary>Where in the clip it is.</summary>
    public TimeSpan Position => position;

    /// <summary>How long the clip plays for, by its header; zero until the header has arrived.</summary>
    public TimeSpan Duration => clip.Clip?.Duration ?? TimeSpan.Zero;

    /// <summary>
    /// While waiting, how long until playing can start as far as can be told; null when it cannot be told yet
    /// or the player is not waiting.
    /// </summary>
    public TimeSpan? StartsIn { get; private set; }

    /// <summary>Plays from where it is, or from the start once it has ended.</summary>
    public void Play()
    {
        wanted = true;
        if (Phase == PlayerPhase.Ended)
        {
            position = TimeSpan.Zero;
            shown = -1;
        }

        if (Phase is PlayerPhase.Paused or PlayerPhase.Ended)
        {
            Phase = PlayerPhase.Waiting;
        }
    }

    /// <summary>Stops where it is.</summary>
    public void Pause()
    {
        wanted = false;
        if (Phase == PlayerPhase.Playing)
        {
            position = Playhead(now());
        }

        if (Phase != PlayerPhase.Ended)
        {
            Phase = PlayerPhase.Paused;
        }

        soundRestarts = true;
    }

    /// <summary>Goes to <paramref name="to"/>, as far as the clip has arrived.</summary>
    public void Seek(TimeSpan to)
    {
        var ready = clip.Ready;
        position = to < TimeSpan.Zero ? TimeSpan.Zero : to > ready ? ready : to;
        shown = -1;
        soundRestarts = true;
        if (Phase == PlayerPhase.Playing)
        {
            startedAt = now();
            startedFrom = position;
        }
        else if (Phase == PlayerPhase.Ended)
        {
            Phase = wanted ? PlayerPhase.Waiting : PlayerPhase.Paused;
        }
    }

    /// <summary>Moves the player on to the present, and says what to show and play.</summary>
    public PlayerStep Step()
    {
        var wall = now();
        Measure(wall);
        if (Phase == PlayerPhase.Waiting && CanPlay(wall))
        {
            Phase = PlayerPhase.Playing;
            startedAt = wall;
            startedFrom = position;
            soundRestarts = true;
        }
        else if (Phase == PlayerPhase.Playing)
        {
            position = Playhead(wall);
            var ready = clip.Ready;
            if (position >= ready)
            {
                position = ready;
                Phase = clip.IsComplete ? PlayerPhase.Ended : PlayerPhase.Waiting;
                soundRestarts = true;
            }
        }

        TimedPicture? picture = null;
        if (clip.PictureAt(position) is { } due && due.Number != shown)
        {
            shown = due.Number;
            picture = due.Picture;
        }

        var restarts = soundRestarts;
        IReadOnlyList<SoundSlice> sound = [];
        if (Phase == PlayerPhase.Playing)
        {
            // From where it plays after a jump; otherwise every run that has arrived since, counted exactly.
            (sound, soundRunsQueued) = restarts ? clip.SoundFrom(position) : clip.SoundAfter(soundRunsQueued);
            soundRestarts = false;
        }

        return new PlayerStep(picture, sound, restarts && Phase == PlayerPhase.Playing);
    }

    private TimeSpan Playhead(TimeSpan wall) => startedFrom + (wall - startedAt);

    /// <summary>Notes how much of the clip has arrived by now, keeping the last few seconds of that.</summary>
    private void Measure(TimeSpan wall)
    {
        arrivals.Enqueue((wall, clip.BytesRead));
        while (arrivals.Count > 2 && wall - arrivals.Peek().Wall > RateWindow)
        {
            arrivals.Dequeue();
        }
    }

    /// <summary>
    /// Whether playing can start from where the player is: the whole clip is here, or, at the rate its bytes have
    /// been coming, the last of them will be here a lead before the clip reaches them, with a lead ready already.
    /// </summary>
    private bool CanPlay(TimeSpan wall)
    {
        StartsIn = null;
        if (clip.Clip is null)
        {
            return false;
        }

        if (clip.IsComplete)
        {
            return true;
        }

        var (firstWall, firstBytes) = arrivals.Peek();
        var watched = wall - firstWall;
        if (watched < RateNeeds)
        {
            return false;
        }

        var perSecond = (clip.BytesRead - firstBytes) / watched.TotalSeconds;
        if (perSecond <= 0)
        {
            return false;
        }

        var rest = TimeSpan.FromSeconds(Math.Max(0, length - clip.BytesRead) / perSecond);
        var wait = rest + lead - (Duration - position);
        if (wait > TimeSpan.Zero)
        {
            StartsIn = wait;
            return false;
        }

        // The rest is in time; the start must not be on the very edge of what has arrived either.
        return clip.Ready - position >= lead;
    }
}
