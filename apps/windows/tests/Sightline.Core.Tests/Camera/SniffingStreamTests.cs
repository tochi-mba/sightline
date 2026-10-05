using Shouldly;
using Sightline.Core.Camera;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>Holding a download's first bytes back until they say what the file is.</summary>
public sealed class SniffingStreamTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private readonly List<MediaKind> opened = [];
    private readonly Destination destination = new();

    public void Dispose() => destination.Dispose();

    private SniffingStream Sniffing() => new(kind =>
    {
        opened.Add(kind);
        return destination;
    });

    [Fact]
    public void Nothing_is_opened_until_the_first_bytes_say_what_the_file_is()
    {
        using var stream = Sniffing();

        stream.Write(Jpeg, 0, 5);
        opened.ShouldBeEmpty();
        stream.Write(Jpeg, 5, 10);

        opened.ShouldBe([MediaKind.Jpeg]);
        destination.ToArray().ShouldBe(Jpeg);
        stream.Finish();
        opened.Count.ShouldBe(1);
    }

    [Fact]
    public void A_file_shorter_than_a_header_is_opened_at_the_end_and_an_empty_one_never()
    {
        using var empty = Sniffing();
        empty.Finish();
        opened.ShouldBeEmpty();

        using var stream = Sniffing();
        stream.Write([1, 2, 3, 4]);
        stream.Finish();

        opened.ShouldBe([MediaKind.Unknown]);
        destination.ToArray().ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public void The_disk_failing_at_any_point_is_a_save_failure()
    {
        using var refused = new SniffingStream(_ => throw new IOException("The disk is full."));
        Should.Throw<SaveFailureException>(() => refused.Write(Jpeg)).Message.ShouldBe("The disk is full.");
        using var denied = new SniffingStream(_ => throw new UnauthorizedAccessException("Access to the path is denied."));
        Should.Throw<SaveFailureException>(() => denied.Write(Jpeg)).InnerException.ShouldBeOfType<UnauthorizedAccessException>();

        using var stream = Sniffing();
        stream.Write(Jpeg);
        destination.Fails = true;
        Should.Throw<SaveFailureException>(() => stream.Write(Jpeg)).InnerException.ShouldBeOfType<IOException>();
        destination.Denies = true;
        Should.Throw<SaveFailureException>(() => stream.Write(Jpeg)).InnerException.ShouldBeOfType<UnauthorizedAccessException>();
    }

    [Fact]
    public void It_only_writes_and_flushes_what_it_opened()
    {
        using var stream = Sniffing();
        stream.CanRead.ShouldBeFalse();
        stream.CanSeek.ShouldBeFalse();
        stream.CanWrite.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => stream.Length);
        Should.Throw<NotSupportedException>(() => stream.Position);
        Should.Throw<NotSupportedException>(() => stream.Position = 0);
        Should.Throw<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Should.Throw<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => stream.SetLength(0));

        stream.Flush();
        destination.Flushes.ShouldBe(0);
        stream.Write(Jpeg);
        stream.Flush();
        destination.Flushes.ShouldBe(1);
    }

    private sealed class Destination : MemoryStream
    {
        public bool Fails { get; set; }

        public bool Denies { get; set; }

        public int Flushes { get; private set; }

        // MemoryStream's span overload calls the array one for a derived type, so only the array one checks.
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Denies)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }

            if (Fails)
            {
                throw new IOException("The device is not ready.");
            }

            base.Write(buffer, offset, count);
        }

        public override void Flush() => Flushes++;
    }
}
