using Sightline.Core.Camera;

namespace Sightline.Core.Playback;

/// <summary>
/// Where a clip's download goes: into the file it is kept in, and then to the reader that times it, so every
/// byte the reader knows of is already in the file for a player to read. The disk's failures become
/// <see cref="SaveFailureException"/>, told apart from the camera's.
/// </summary>
internal sealed class ClipTee(Stream file, ClipReader reader) : Stream
{
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
        try
        {
            file.Write(buffer);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new SaveFailureException(failure.Message, failure);
        }

        reader.Push(buffer);
    }

    public override void Flush() => file.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
