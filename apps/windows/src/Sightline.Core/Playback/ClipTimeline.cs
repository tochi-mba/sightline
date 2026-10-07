using Sightline.Protocol.Media;

namespace Sightline.Core.Playback;

/// <summary>One picture of a clip: when it shows, and where its JPEG lies in the file.</summary>
/// <param name="At">When it shows, from the start of the clip.</param>
/// <param name="Offset">Where its bytes start in the file.</param>
/// <param name="Length">How many bytes it is.</param>
public readonly record struct TimedPicture(TimeSpan At, long Offset, int Length);

/// <summary>One run of a clip's sound: when it starts, and where its samples lie in the file.</summary>
/// <param name="At">When it starts, from the start of the clip.</param>
/// <param name="Offset">Where its bytes start in the file.</param>
/// <param name="Length">How many bytes it is.</param>
public readonly record struct TimedSound(TimeSpan At, long Offset, int Length);

/// <summary>
/// When each picture of a clip is shown, worked out as the clip arrives off the card.
/// </summary>
/// <remarks>
/// <para>
/// A clip's header says 30 pictures a second, but the reference camera takes about 25 and makes up the rest by
/// repeating some, which only the index at the very end of the file says (measured 2026-10-07: 122 pictures
/// filling 149 places). A clip still arriving has no index yet, so its sound keeps time instead. The camera
/// writes a run of sound every half second, after the pictures it took during that run, so the pictures
/// between two runs of sound are spread evenly across the second run.
/// </para>
/// <para>
/// Pictures after the last run of sound are spread at the rate the clip kept until then, once the clip is
/// known to have ended. A clip with no sound, or sound that is not plain PCM, is timed by its header's rate.
/// </para>
/// </remarks>
public sealed class ClipTimeline
{
    private readonly AviClip clip;
    private readonly double soundBytesPerSecond;
    private readonly List<TimedPicture> pictures = [];
    private readonly List<TimedSound> sounds = [];
    private readonly List<AviChunk.Picture> waiting = [];
    private TimeSpan soundTime;

    /// <summary>A timeline for a clip whose headers are <paramref name="clip"/>.</summary>
    public ClipTimeline(AviClip clip)
    {
        this.clip = clip ?? throw new ArgumentNullException(nameof(clip));
        soundBytesPerSecond = clip.Sound is { } sound ? sound.SampleRate * sound.Channels * (sound.BitsPerSample / 8.0) : 0;
    }

    /// <summary>The clip's headers.</summary>
    public AviClip Clip => clip;

    /// <summary>Every picture timed so far, in the order they show.</summary>
    public IReadOnlyList<TimedPicture> Pictures => pictures;

    /// <summary>Every run of sound timed so far, in the order they play.</summary>
    public IReadOnlyList<TimedSound> Sounds => sounds;

    /// <summary>How far into the clip everything is timed: playing can go this far without waiting.</summary>
    public TimeSpan Ready { get; private set; }

    /// <summary>Whether the whole clip has arrived and been timed.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Times the next piece of the clip, in the order it is stored.</summary>
    public void Add(AviChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        switch (chunk)
        {
            case AviChunk.Sound sound when soundBytesPerSecond > 0:
                var length = TimeSpan.FromSeconds(sound.Pcm.Length / soundBytesPerSecond);
                Spread(soundTime, length);
                sounds.Add(new TimedSound(soundTime, sound.Offset, sound.Pcm.Length));
                soundTime += length;
                Ready = soundTime;
                break;
            case AviChunk.Picture picture when soundBytesPerSecond > 0:
                waiting.Add(picture);
                break;
            case AviChunk.Picture picture:
                pictures.Add(new TimedPicture(clip.TimeOf(pictures.Count), picture.Offset, picture.Jpeg.Length));
                Ready = clip.TimeOf(pictures.Count);
                break;
        }
    }

    /// <summary>Says the clip has ended, timing the pictures after its last run of sound.</summary>
    public void Finish()
    {
        if (waiting.Count > 0)
        {
            var each = pictures.Count > 0
                ? soundTime / pictures.Count
                : clip.TimeOf(1);
            var length = each * waiting.Count;
            Spread(soundTime, length);
            Ready = soundTime + length;
        }

        IsComplete = true;
    }

    /// <summary>The number of the picture showing at <paramref name="position"/>: the last one due by then, or -1 before the first.</summary>
    public int PictureAt(TimeSpan position)
    {
        var low = 0;
        var high = pictures.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (pictures[middle].At <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    /// <summary>Spreads the pictures waiting for a time evenly from <paramref name="from"/> over <paramref name="length"/>.</summary>
    private void Spread(TimeSpan from, TimeSpan length)
    {
        for (var i = 0; i < waiting.Count; i++)
        {
            pictures.Add(new TimedPicture(from + (length * i / waiting.Count), waiting[i].Offset, waiting[i].Jpeg.Length));
        }

        waiting.Clear();
    }
}
