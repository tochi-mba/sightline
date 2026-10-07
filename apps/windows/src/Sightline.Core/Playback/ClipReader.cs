using Sightline.Protocol.Media;

namespace Sightline.Core.Playback;

/// <summary>A stretch of a clip's sound to play: when it starts, and where its samples lie in the file.</summary>
/// <param name="At">When it starts, from the start of the clip.</param>
/// <param name="Offset">Where the samples start in the file.</param>
/// <param name="Length">How many bytes of samples.</param>
public readonly record struct SoundSlice(TimeSpan At, long Offset, int Length);

/// <summary>
/// A clip coming off the card, read and timed as its bytes arrive, for a player to play while the rest is
/// still on its way.
/// </summary>
/// <remarks>
/// The download writes into this from one thread while a player reads it from another, so every member takes
/// the same lock. What the player needs back is small: where a picture lies in the file, not the picture.
/// </remarks>
public sealed class ClipReader
{
    private readonly Lock gate = new();
    private readonly AviReader avi = new();
    private ClipTimeline? timeline;
    private long bytesRead;

    /// <summary>The clip's headers, once they have arrived.</summary>
    public AviClip? Clip
    {
        get
        {
            lock (gate)
            {
                return timeline?.Clip;
            }
        }
    }

    /// <summary>How many bytes have arrived.</summary>
    public long BytesRead
    {
        get
        {
            lock (gate)
            {
                return bytesRead;
            }
        }
    }

    /// <summary>How far into the clip everything has arrived and been timed.</summary>
    public TimeSpan Ready
    {
        get
        {
            lock (gate)
            {
                return timeline?.Ready ?? TimeSpan.Zero;
            }
        }
    }

    /// <summary>Whether the whole clip has arrived.</summary>
    public bool IsComplete
    {
        get
        {
            lock (gate)
            {
                return timeline?.IsComplete ?? false;
            }
        }
    }

    /// <summary>Takes the next bytes of the file.</summary>
    /// <exception cref="InvalidDataException">The file is not a clip this can play.</exception>
    public void Push(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            bytesRead += bytes.Length;
            foreach (var chunk in avi.Push(bytes))
            {
                // The headers come before any picture or sound, so they are in by now.
                (timeline ??= new ClipTimeline(avi.Clip!)).Add(chunk);
            }

            if (timeline is null && avi.Clip is { } clip)
            {
                timeline = new ClipTimeline(clip);
            }
        }
    }

    /// <summary>Says the whole file has arrived.</summary>
    /// <exception cref="InvalidDataException">It ended before its headers did.</exception>
    public void Finish()
    {
        lock (gate)
        {
            (timeline ?? throw new InvalidDataException("The clip ended before its headers did.")).Finish();
        }
    }

    /// <summary>The picture showing at <paramref name="position"/>, with its number, or null before the first.</summary>
    public (int Number, TimedPicture Picture)? PictureAt(TimeSpan position)
    {
        lock (gate)
        {
            if (timeline is null)
            {
                return null;
            }

            var number = timeline.PictureAt(position);
            return number < 0 ? null : (number, timeline.Pictures[number]);
        }
    }

    /// <summary>
    /// The sound from <paramref name="position"/> on, as far as it has arrived: the rest of the run playing at
    /// that moment, then every run after it; and how many runs have arrived, which is where to carry on from.
    /// </summary>
    public (IReadOnlyList<SoundSlice> Slices, int Runs) SoundFrom(TimeSpan position)
    {
        lock (gate)
        {
            var slices = new List<SoundSlice>();
            if (timeline?.Clip.Sound is not { } sound)
            {
                return (slices, 0);
            }

            var bytesPerSecond = sound.SampleRate * sound.Channels * (sound.BitsPerSample / 8);
            var frame = sound.Channels * (sound.BitsPerSample / 8);
            foreach (var run in timeline.Sounds)
            {
                var end = run.At + TimeSpan.FromSeconds((double)run.Length / bytesPerSecond);
                if (end <= position)
                {
                    continue;
                }

                // Into the run by whole sample frames, so the samples stay aligned.
                var skip = run.At >= position ? 0 : (int)((position - run.At).TotalSeconds * bytesPerSecond) / frame * frame;
                var at = run.At + TimeSpan.FromSeconds((double)skip / bytesPerSecond);
                slices.Add(new SoundSlice(at, run.Offset + skip, run.Length - skip));
            }

            return (slices, timeline.Sounds.Count);
        }
    }

    /// <summary>Every whole run of sound from run number <paramref name="first"/> on, and how many runs have arrived.</summary>
    public (IReadOnlyList<SoundSlice> Slices, int Runs) SoundAfter(int first)
    {
        lock (gate)
        {
            var runs = timeline?.Sounds ?? [];
            return ([.. runs.Skip(first).Select(run => new SoundSlice(run.At, run.Offset, run.Length))], runs.Count);
        }
    }
}
