using Shouldly;
using Sightline.Core.Camera;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>Copies from the card, saved into a folder on this PC.</summary>
public sealed class FolderSinkTests : IDisposable
{
    private static readonly CameraFile Photo = new('J', 1, new DateTime(2026, 10, 4, 18, 35, 56), 3);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "sightline-sink-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Save(FolderSink sink, byte[] bytes)
    {
        var pending = sink.Create(Photo, MediaKind.Jpeg);
        pending.Output.Write(bytes);
        return pending.Publish();
    }

    [Fact]
    public void A_copy_is_hidden_until_whole_then_named_for_what_it_is()
    {
        var sink = new FolderSink(folder);
        var pending = sink.Create(Photo, MediaKind.Jpeg);
        pending.Output.Write([1, 2, 3]);

        var hidden = Directory.GetFiles(folder).Single();
        Path.GetFileName(hidden).ShouldStartWith(".");
        File.GetAttributes(hidden).HasFlag(FileAttributes.Hidden).ShouldBeTrue();

        var saved = pending.Publish();

        saved.ShouldBe(Path.Combine(folder, Photo.DisplayName + ".jpg"));
        File.ReadAllBytes(saved).ShouldBe([1, 2, 3]);
        File.GetAttributes(saved).HasFlag(FileAttributes.Hidden).ShouldBeFalse();
        Directory.GetFiles(folder).ShouldBe([saved]);
        sink.Folder.ShouldBe(folder);
    }

    [Fact]
    public void Nothing_already_there_is_replaced()
    {
        var sink = new FolderSink(folder);

        var first = Save(sink, [1]);
        var second = Save(sink, [2]);
        var third = Save(sink, [3]);

        Path.GetFileName(second).ShouldBe(Photo.DisplayName + "-2.jpg");
        Path.GetFileName(third).ShouldBe(Photo.DisplayName + "-3.jpg");
        File.ReadAllBytes(first).ShouldBe([1]);
    }

    [Fact]
    public void A_copy_thrown_away_leaves_nothing()
    {
        var pending = new FolderSink(folder).Create(Photo, MediaKind.Avi);
        pending.Output.Write([1, 2]);

        pending.Discard();

        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public void A_folder_that_cannot_be_made_is_an_io_error()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        File.WriteAllText(folder, "a file where the folder should be");
        try
        {
            Should.Throw<IOException>(() => new FolderSink(folder).Create(Photo, MediaKind.Jpeg));
        }
        finally
        {
            File.Delete(folder);
        }
    }

    [Fact]
    public void It_needs_a_folder_and_a_file()
    {
        Should.Throw<ArgumentException>(() => new FolderSink(" "));
        Should.Throw<ArgumentNullException>(() => new FolderSink(folder).Create(null!, MediaKind.Jpeg));
        FolderSink.DefaultFolder.ShouldEndWith(Path.Combine("Downloads", "Sightline"));
    }
}
