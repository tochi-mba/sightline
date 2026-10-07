using Sightline.Protocol.Media;

namespace Sightline.Core.Playback;

/// <summary>A sound device that plays PCM in the order it is given, without gaps between pieces.</summary>
public interface ISoundDevice : IDisposable
{
    /// <summary>How many bytes it holds that have not finished playing.</summary>
    long Holding { get; }

    /// <summary>Plays <paramref name="pcm"/> after everything it holds.</summary>
    void Play(byte[] pcm);

    /// <summary>Goes quiet at once, dropping everything it holds.</summary>
    void Stop();
}

/// <summary>
/// Plays a clip's sound as its player says, keeping the device about a second ahead and no further: a long clip's
/// sound is never all in memory, and a pause or a jump is heard at once.
/// </summary>
/// <remarks>
/// The device keeps its own time once it has the sound, which holds with the player's clock to well within a
/// picture over the length of a clip. So the sound is never nudged: it is dropped and started again from the right
/// place whenever the player stops, waits or jumps, which the player says.
/// </remarks>
public sealed class SoundFeed
{
    private readonly ISoundDevice device;
    private readonly Func<long, int, byte[]> read;
    private readonly long ahead;
    private readonly Queue<SoundSlice> waiting = new();
    private bool sounding;

    /// <summary>A feed into <paramref name="device"/> of sound in <paramref name="format"/>.</summary>
    /// <param name="device">Where the sound plays.</param>
    /// <param name="format">The clip's sound, which says how many bytes a second of it takes.</param>
    /// <param name="read">Reads the bytes at an offset in the clip, so many of them.</param>
    public SoundFeed(ISoundDevice device, AviSound format, Func<long, int, byte[]> read)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(format);
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        ahead = (long)format.SampleRate * format.Channels * (format.BitsPerSample / 8);
    }

    /// <summary>Follows one step of the player, which was left in <paramref name="phase"/>, then tops the device up.</summary>
    public void Follow(PlayerPhase phase, PlayerStep step)
    {
        if (sounding && (phase != PlayerPhase.Playing || step.SoundRestarts))
        {
            device.Stop();
            waiting.Clear();
            sounding = false;
        }

        foreach (var slice in step.Sound)
        {
            waiting.Enqueue(slice);
        }

        while (device.Holding < ahead && waiting.TryDequeue(out var next))
        {
            device.Play(read(next.Offset, next.Length));
            sounding = true;
        }
    }
}
