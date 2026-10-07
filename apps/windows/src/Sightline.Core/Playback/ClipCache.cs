using System.Globalization;
using Sightline.Protocol.GpSock;

namespace Sightline.Core.Playback;

/// <summary>
/// Clips played from the card, kept on this PC so playing one again needs no download.
/// </summary>
/// <remarks>
/// <para>
/// A clip being fetched is written to a <c>.part</c> file a player can read while it grows, and gets its real
/// name only once whole, so a clip cut off half-way is never taken for a whole one. A clip is known by its card
/// name, its time and its size, so a file the camera reuses an index for is not mistaken for the old one.
/// </para>
/// <para>
/// The folder is kept under <see cref="Limit"/> bytes by letting go of the clips played longest ago first; a
/// clip bigger than the limit on its own is still kept, alone. The files are this app's, in its own data
/// folder; nothing the person saved is ever touched.
/// </para>
/// </remarks>
public sealed class ClipCache
{
    /// <summary>How much the cache keeps unless told otherwise: two gigabytes, a few minutes of 1080p.</summary>
    public const long DefaultLimit = 2L * 1024 * 1024 * 1024;

    private const int ReadPiece = 64 * 1024;

    /// <summary>A cache in <paramref name="folder"/>, made when the first clip arrives, kept under <paramref name="limit"/> bytes.</summary>
    public ClipCache(string folder, long limit = DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Folder = folder;
        Limit = limit;
    }

    /// <summary>The folder the clips are kept in.</summary>
    public string Folder { get; }

    /// <summary>How many bytes of clips are kept at most.</summary>
    public long Limit { get; }

    /// <summary>
    /// The clip kept for <paramref name="file"/>, read back away from the caller's thread; null when none is kept.
    /// </summary>
    public CardClip? Open(CameraFile file)
    {
        var path = PathFor(file);
        try
        {
            // Played again: it is the last to go.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception missing) when (missing is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var clip = new CardClip(file);
        clip.Arriving(path);
        _ = Task.Run(() => ReadBackAsync(clip, path));
        return clip;
    }

    /// <summary>
    /// Starts keeping <paramref name="file"/>: making room for it first, then a <c>.part</c> file to write it into,
    /// which a player may read while it is written.
    /// </summary>
    public PendingClip Start(CameraFile file)
    {
        var final = PathFor(file);
        Directory.CreateDirectory(Folder);
        MakeRoom(file.ApproximateBytes);
        var part = Path.Combine(Folder, $".{Path.GetFileName(final)}-{Guid.NewGuid():N}.part");
        // Unbuffered, so every byte written is there for a reader at once; shared for reading, and for the
        // rename at the end while a reader still has it open.
        var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
        return new PendingClip(part, final, output);
    }

    /// <summary>Where the whole clip for <paramref name="file"/> is kept.</summary>
    public string PathFor(CameraFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var taken = file.Taken?.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) ?? "undated";
        return Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"{file.DisplayName}-{taken}-{file.SizeKilobytes}k.avi"));
    }

    /// <summary>Deletes <paramref name="file"/> if nothing has it open without sharing it; says whether it did.</summary>
    internal static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads a kept clip into <paramref name="clip"/>, closing the file before saying how it ended.</summary>
    internal static async Task ReadBackAsync(CardClip clip, string path)
    {
        try
        {
            await using (var input = clip.OpenRead())
            {
                var buffer = new byte[ReadPiece];
                int read;
                while ((read = await input.ReadAsync(buffer, clip.Stopping).ConfigureAwait(false)) > 0)
                {
                    clip.Reader.Push(buffer.AsSpan(0, read));
                }
            }

            clip.Reader.Finish();
            clip.Kept(path);
        }
        catch (OperationCanceledException) when (clip.Stopping.IsCancellationRequested)
        {
            clip.Stopped();
        }
        catch (InvalidDataException)
        {
            // It was read whole before it was kept, so the disk has damaged it since. Letting it go means the
            // next play fetches it from the card again rather than failing here every time.
            TryDelete(new FileInfo(path));
            clip.Failed("The copy kept on this PC was damaged, so it was thrown away. Play the clip again to fetch it from the card.");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            clip.Failed($"The copy kept on this PC could not be read: {failure.Message}");
        }
    }

    /// <summary>
    /// Lets go of the pieces of fetches that never finished, then of the clips played longest ago until
    /// <paramref name="needed"/> more bytes fit under the limit. A file in use is left for another time.
    /// </summary>
    private void MakeRoom(long needed)
    {
        var folder = new DirectoryInfo(Folder);
        foreach (var piece in folder.GetFiles("*.part"))
        {
            TryDelete(piece);
        }

        var kept = folder.GetFiles("*.avi").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = kept.Sum(f => f.Length);
        foreach (var oldest in kept)
        {
            if (total + needed <= Limit)
            {
                break;
            }

            // Read before deleting: a deleted file has no length to ask for.
            var length = oldest.Length;
            if (TryDelete(oldest))
            {
                total -= length;
            }
        }
    }
}

/// <summary>A clip being written into the cache; exactly one of <see cref="Keep"/> and <see cref="Discard"/> ends it.</summary>
public sealed class PendingClip : IDisposable
{
    private readonly string final;
    private readonly FileStream output;

    internal PendingClip(string part, string final, FileStream output)
    {
        Path = part;
        this.final = final;
        this.output = output;
    }

    /// <summary>Where the bytes are while it is written, which a player reads.</summary>
    public string Path { get; }

    /// <summary>Where the bytes are written.</summary>
    public Stream Output => output;

    /// <summary>Gives the whole clip its real name, and says where that is.</summary>
    public string Keep()
    {
        output.Dispose();
        File.Move(Path, final, overwrite: true);
        return final;
    }

    /// <summary>Throws away what was written; a piece that cannot be deleted now goes when the cache next makes room.</summary>
    public void Discard()
    {
        output.Dispose();
        ClipCache.TryDelete(new FileInfo(Path));
    }

    /// <inheritdoc />
    public void Dispose() => output.Dispose();
}
