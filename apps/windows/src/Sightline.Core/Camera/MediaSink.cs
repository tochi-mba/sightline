using Sightline.Protocol.GpSock;

namespace Sightline.Core.Camera;

/// <summary>
/// What kind of file a download turned out to be, from its first bytes: the camera's list says photo or
/// video but names no container. The same three the Android app recognises.
/// </summary>
public enum MediaKind
{
    /// <summary>A JPEG photo.</summary>
    Jpeg,

    /// <summary>An AVI video.</summary>
    Avi,

    /// <summary>An MP4 or MOV video.</summary>
    Mp4,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>Recognising a file from its first bytes.</summary>
public static class MediaKinds
{
    /// <summary>How many bytes <see cref="Sniff"/> needs to tell every kind apart.</summary>
    public const int HeaderLength = 12;

    /// <summary>The kind whose signature <paramref name="header"/> starts with; Unknown when none or too short to tell.</summary>
    public static MediaKind Sniff(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return MediaKind.Jpeg;
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8))
        {
            return MediaKind.Avi;
        }

        return header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8) ? MediaKind.Mp4 : MediaKind.Unknown;
    }

    /// <summary>The extension a file of <paramref name="kind"/> is saved with.</summary>
    public static string Extension(this MediaKind kind) => kind switch
    {
        MediaKind.Jpeg => ".jpg",
        MediaKind.Avi => ".avi",
        MediaKind.Mp4 => ".mp4",
        _ => ".bin",
    };
}

/// <summary>Where downloads from the card are saved: a folder on this PC.</summary>
public interface IMediaSink
{
    /// <summary>Starts saving <paramref name="file"/>, which <paramref name="kind"/> says the bytes are.</summary>
    IPendingMedia Create(CameraFile file, MediaKind kind);
}

/// <summary>A file being saved; exactly one of <see cref="Publish"/> and <see cref="Discard"/> ends it.</summary>
public interface IPendingMedia
{
    /// <summary>Where the bytes are written.</summary>
    Stream Output { get; }

    /// <summary>Makes the whole file visible under its final name, and says where it went.</summary>
    string Publish();

    /// <summary>Throws away what was written.</summary>
    void Discard();
}

/// <summary>
/// The PC could not save a file: its disk is full, or Windows denied the folder. Kept apart from the
/// camera's failures, which are <see cref="IOException"/>s too, because a full disk is no reason to think
/// the camera was lost.
/// </summary>
public sealed class SaveFailureException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SaveFailureException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SaveFailureException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SaveFailureException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A stream that holds back a download's first bytes until it knows what they are, then opens the real
/// destination for that kind and passes everything through. Nothing is created for a download that fails
/// before its first bytes, and the disk's failures become <see cref="SaveFailureException"/>.
/// </summary>
internal sealed class SniffingStream(Func<MediaKind, Stream> open) : Stream
{
    private readonly byte[] header = new byte[MediaKinds.HeaderLength];
    private int held;
    private Stream? target;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (target is { } opened)
        {
            try
            {
                opened.Write(buffer);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw new SaveFailureException(failure.Message, failure);
            }

            return;
        }

        var taken = Math.Min(buffer.Length, header.Length - held);
        buffer[..taken].CopyTo(header.AsSpan(held));
        held += taken;
        if (held == header.Length)
        {
            var started = Start();
            if (buffer.Length > taken)
            {
                var rest = buffer[taken..].ToArray();
                Saving(() => started.Write(rest));
            }
        }
    }

    /// <summary>Opens the destination with whatever was held back, even short of a full header.</summary>
    public void Finish()
    {
        if (target is null && held > 0)
        {
            Start();
        }
    }

    public override void Flush() => target?.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private Stream Start()
    {
        var kind = MediaKinds.Sniff(header.AsSpan(0, held));
        var opened = Saving(() => open(kind));
        target = opened;
        Saving(() => opened.Write(header, 0, held));
        return opened;
    }

    private static void Saving(Action write) => Saving(() =>
    {
        write();
        return 0;
    });

    private static T Saving<T>(Func<T> write)
    {
        try
        {
            return write();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new SaveFailureException(failure.Message, failure);
        }
    }
}
