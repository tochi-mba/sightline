using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Playback;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>A clip's download, written to the disk and then read, in that order.</summary>
public sealed class ClipTeeTests
{
    private static readonly byte[] Clip = FakeClip.Reference([FakeRtspCamera.Jpeg(500)], []).Build();

    [Fact]
    public void Each_piece_is_on_the_disk_before_the_reader_hears_of_it()
    {
        var reader = new ClipReader();
        using var file = new Watching(reader);
        using var tee = new ClipTee(file, reader);

        tee.Write(Clip, 0, 100);
        tee.Write(Clip.AsSpan(100));
        tee.Flush();

        file.ReaderHadHeard.ShouldBe([0, 100]);
        file.ToArray().ShouldBe(Clip);
        reader.BytesRead.ShouldBe(Clip.Length);
        reader.Clip.ShouldNotBeNull();
    }

    [Fact]
    public void The_disk_failing_is_a_save_failure_and_the_reader_never_hears_of_the_piece()
    {
        var reader = new ClipReader();
        using var fullDisk = new Failing(new IOException("There is not enough space on the disk."));
        using var full = new ClipTee(fullDisk, reader);
        Should.Throw<SaveFailureException>(() => full.Write(Clip)).Message.ShouldBe("There is not enough space on the disk.");

        using var deniedFolder = new Failing(new UnauthorizedAccessException("Access to the path is denied."));
        using var denied = new ClipTee(deniedFolder, reader);
        Should.Throw<SaveFailureException>(() => denied.Write(Clip)).InnerException.ShouldBeOfType<UnauthorizedAccessException>();

        reader.BytesRead.ShouldBe(0);
    }

    [Fact]
    public void It_only_writes_forward()
    {
        using var tee = new ClipTee(Stream.Null, new ClipReader());

        tee.CanRead.ShouldBeFalse();
        tee.CanSeek.ShouldBeFalse();
        tee.CanWrite.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => tee.Length);
        Should.Throw<NotSupportedException>(() => tee.Position);
        Should.Throw<NotSupportedException>(() => tee.Position = 1);
        Should.Throw<NotSupportedException>(() => tee.Read(new byte[1], 0, 1));
        Should.Throw<NotSupportedException>(() => tee.Seek(0, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => tee.SetLength(1));
    }

    /// <summary>A disk that notes how much the reader had heard of each time it is written to.</summary>
    private sealed class Watching(ClipReader reader) : MemoryStream
    {
        public List<long> ReaderHadHeard { get; } = [];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ReaderHadHeard.Add(reader.BytesRead);
            base.Write(buffer);
        }
    }

    private sealed class Failing(Exception failure) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw failure;
    }
}
