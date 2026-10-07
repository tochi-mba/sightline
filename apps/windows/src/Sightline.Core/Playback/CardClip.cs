using Sightline.Protocol.GpSock;

namespace Sightline.Core.Playback;

/// <summary>How a clip from the card stopped arriving.</summary>
public abstract record ClipArrival
{
    private ClipArrival()
    {
    }

    /// <summary>All of it arrived, and it is kept at <paramref name="Where"/>.</summary>
    public sealed record Kept(string Where) : ClipArrival;

    /// <summary>It stopped arriving part-way, because of <paramref name="Reason"/>.</summary>
    public sealed record Failed(string Reason) : ClipArrival;

    /// <summary>The player let it go before all of it had arrived.</summary>
    public sealed record Stopped : ClipArrival
    {
        /// <summary>The one value.</summary>
        public static Stopped Instance { get; } = new();
    }
}

/// <summary>
/// A video from the card, arriving for a player: read and timed as it comes, and kept on this PC once whole.
/// </summary>
/// <remarks>
/// The bytes go to a file rather than memory, a long clip being far bigger than a picture: <see cref="OpenRead"/>
/// opens it for the player, which reads each picture from where <see cref="Reader"/> says it lies, while the
/// rest is still being written. The player disposes the clip when it closes, which stops a fetch still running.
/// </remarks>
public sealed class CardClip : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<ClipArrival> arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? path;
    private int disposed;

    internal CardClip(CameraFile file)
    {
        File = file;
        Stopping = stopping.Token;
    }

    /// <summary>The video on the card.</summary>
    public CameraFile File { get; }

    /// <summary>The clip as it arrives: its headers, and when each picture and run of sound plays.</summary>
    public ClipReader Reader { get; } = new();

    /// <summary>Completes once the clip stops arriving: all of it kept, or why not.</summary>
    public Task<ClipArrival> Arrival => arrival.Task;

    /// <summary>Whether the clip's bytes have started arriving, so <see cref="OpenRead"/> has something to open.</summary>
    public bool HasBytes => path is not null;

    /// <summary>Signalled when the clip is let go.</summary>
    internal CancellationToken Stopping { get; }

    /// <summary>Opens the clip's bytes for reading, as far as they have arrived and on as they come.</summary>
    /// <exception cref="InvalidOperationException">No bytes have arrived yet.</exception>
    public Stream OpenRead() => new FileStream(
        path ?? throw new InvalidOperationException("The clip has not started arriving."),
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Lets the clip go: if it is still arriving that stops, and what had arrived is thrown away. A clip already
    /// kept stays kept.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stopping.Cancel();
            stopping.Dispose();
        }
    }

    internal void Arriving(string at) => path = at;

    internal void Kept(string at)
    {
        path = at;
        arrival.TrySetResult(new ClipArrival.Kept(at));
    }

    internal void Failed(string reason) => arrival.TrySetResult(new ClipArrival.Failed(reason));

    internal void Stopped() => arrival.TrySetResult(ClipArrival.Stopped.Instance);
}
