using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Testing;
using Sightline.Protocol.GpSock;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>The camera's card: listing it, copying from it and deleting from it.</summary>
public sealed class LibraryTests
{
    private static readonly DateTime Taken = new(2026, 10, 4, 18, 35, 56);
    private static readonly byte[] Photo = FakeRtspCamera.Jpeg(3000);

    private static byte[] Video()
    {
        var bytes = new byte[5000];
        "RIFF"u8.CopyTo(bytes);
        "AVI "u8.CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static async Task<ControllerHarness> WithCardAsync()
    {
        var camera = new ControllerHarness();
        camera.Control.AddFile('J', Taken, Photo);
        camera.Control.AddFile('A', Taken.AddMinutes(1), Video());
        camera.Control.DownloadChunk = 1000;
        await camera.ConnectedAsync();
        return camera;
    }

    private static async Task<IReadOnlyList<CameraFile>> ListedAsync(ControllerHarness camera)
    {
        await camera.Controller.RefreshLibrary()!;
        return camera.State.Library.Files!;
    }

    [Fact]
    public async Task The_card_is_listed_with_thumbnails_and_the_camera_put_back()
    {
        await using var camera = await WithCardAsync();

        var files = await ListedAsync(camera);

        files.Select(f => f.Code).ShouldBe(['J', 'A']);
        camera.State.Library.Thumbnails.Count.ShouldBe(2);
        camera.State.Library.Reading.ShouldBeFalse();
        camera.Control.Mode.ShouldBe(CameraMode.Record);
        camera.Seen.ShouldContain(s => s.Library.Reading);
    }

    [Fact]
    public async Task Reading_the_card_ends_the_live_picture_until_the_camera_restarts()
    {
        await using var camera = await WithCardAsync();
        camera.Controller.HoldLive("window");
        camera.Controller.ShowLivePicture();
        (await camera.UntilAsync(s => s.Live is LiveView.Playing)).HoldsLivePicture.ShouldBeTrue();

        var files = await ListedAsync(camera);

        files.Count.ShouldBe(2);
        camera.Seen.ShouldContain(s => s.Live is LiveView.Paused);
        (await camera.UntilAsync(s => s.Live is LiveView.Unavailable)).HoldsLivePicture.ShouldBeFalse();
        camera.Streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Files_whose_thumbnails_the_camera_will_not_give_are_listed_anyway()
    {
        await using var camera = await WithCardAsync();
        camera.Control.ForcedRefusals[GpSockCommand.PlaybackGetThumbnail] = NakCode.GetThumbnailFail;

        var files = await ListedAsync(camera);

        files.Count.ShouldBe(2);
        camera.State.Library.Thumbnails.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_card_lists_nothing_and_a_refused_list_is_explained()
    {
        await using var empty = new ControllerHarness();
        await empty.ConnectedAsync();
        (await ListedAsync(empty)).ShouldBeEmpty();

        await using var camera = await WithCardAsync();
        camera.Control.ForcedRefusals[GpSockCommand.PlaybackGetFileList] = NakCode.GetFileListFail;
        await camera.Controller.RefreshLibrary()!;
        camera.State.Notice!.Text.ShouldBe("The camera said no: it could not read the file list.");
        camera.Control.Mode.ShouldBe(CameraMode.Record);
    }

    [Fact]
    public async Task Files_are_copied_whole_recognised_and_published()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);
        var sink = new FakeSink();

        await camera.Controller.Download(files, sink)!;

        sink.Created.Select(c => c.Kind).ShouldBe([MediaKind.Jpeg, MediaKind.Avi]);
        sink.Published["Downloads/PICT0001.jpg"].ShouldBe(Photo);
        camera.State.Library.Transfers[files[1]].ShouldBe(new Transfer.Saved("Downloads/MOVI0002.avi"));
        camera.Seen.ShouldContain(s => s.Library.Transfers.Values.OfType<Transfer.Copying>().Any(c => c.Copied == 1000));
    }

    [Fact]
    public async Task A_refused_or_empty_file_is_marked_and_a_full_disk_is_not_a_lost_camera()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);
        var missing = new CameraFile('J', 99, Taken, 1);

        await camera.Controller.Download([missing, files[0]], new FakeSink())!;
        camera.State.Library.Transfers[missing].ShouldBe(new Transfer.Failed("The camera said no: it does not know that command."));
        camera.State.Library.Transfers[files[0]].ShouldBeOfType<Transfer.Saved>();

        await camera.Controller.Download([files[0]], new FakeSink { Full = true })!;
        camera.State.Library.Transfers[files[0]].ShouldBe(new Transfer.Failed("This PC could not save it: There is not enough space on the disk."));
        camera.State.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task An_empty_file_is_reported_and_nothing_saved()
    {
        await using var camera = new ControllerHarness();
        camera.Control.AddFile('J', Taken, []);
        await camera.ConnectedAsync();
        var sink = new FakeSink();

        await camera.Controller.Download(await ListedAsync(camera), sink)!;

        camera.State.Library.Transfers.Values.Single().ShouldBe(new Transfer.Failed("The camera sent nothing for this file."));
        sink.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_copy_that_stalls_is_given_up_and_the_camera_treated_as_lost()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);
        camera.Control.DownloadStallsAfterBytes = 2000;
        var sink = new FakeSink();

        await camera.Controller.Download(files, sink)!;

        var reconnecting = (await camera.UntilAsync(s => s.Connection is Connection.Reconnecting)).Connection.ShouldBeOfType<Connection.Reconnecting>();
        reconnecting.Problem.Detail.ShouldBe("The camera stopped sending for 0 seconds.");
        sink.Discarded.ShouldBe([files[0]]);
        camera.State.Library.Transfers[files[1]].ShouldBe(new Transfer.Failed("Not copied: the copy was stopped."));
    }

    [Fact]
    public async Task Files_safely_copied_can_be_deleted_from_the_card()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);

        await camera.Controller.Download([files[0], new CameraFile('J', 99, Taken, 1)], new FakeSink(), deleteAfter: true)!;
        camera.State.Library.Files!.Select(f => f.Index).ShouldBe([2]);

        camera.Control.RefuseDownloadWith = NakCode.FullStorage;
        await camera.Controller.Download(camera.State.Library.Files!, new FakeSink(), deleteAfter: true)!;
        camera.Control.Files.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_removes_files_and_says_how_many()
    {
        await using var camera = await WithCardAsync();
        camera.Control.AddFile('J', Taken.AddMinutes(2), Photo);
        var files = await ListedAsync(camera);

        await camera.Controller.Delete([files[0], files[2]])!;
        camera.State.Library.Files!.Select(f => f.Index).ShouldBe([2]);
        camera.State.Notice!.Text.ShouldBe("2 files deleted from the card.");
        await camera.Controller.Delete([camera.State.Library.Files![0]])!;

        camera.State.Notice!.Text.ShouldBe("Deleted from the card.");
        camera.Control.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_on_the_card_is_touched_while_the_camera_records()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);
        await camera.Controller.ToggleRecording()!;

        await camera.Controller.RefreshLibrary()!;
        camera.State.Notice!.Text.ShouldBe("Stop recording to look at the card.");
        await camera.Controller.Download(files, new FakeSink())!;
        camera.State.Notice!.Text.ShouldBe("Stop recording to copy from the card.");
        await camera.Controller.Delete(files)!;
        camera.State.Notice!.Text.ShouldBe("Stop recording to delete from the card.");

        camera.Control.Files.Count.ShouldBe(2);
    }

    [Fact]
    public void Files_are_recognised_from_their_first_bytes()
    {
        MediaKinds.Sniff([0xFF, 0xD8, 0xFF, 0xE0]).ShouldBe(MediaKind.Jpeg);
        MediaKinds.Sniff(Video()).ShouldBe(MediaKind.Avi);
        MediaKinds.Sniff([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']).ShouldBe(MediaKind.Mp4);
        MediaKinds.Sniff([0xFF, 0xD8]).ShouldBe(MediaKind.Unknown);
        MediaKinds.Sniff("RIFF\0\0\0\0WAVE"u8).ShouldBe(MediaKind.Unknown);
        MediaKind.Unknown.Extension().ShouldBe(".bin");
        MediaKind.Mp4.Extension().ShouldBe(".mp4");
    }

    [Fact]
    public async Task A_file_the_disk_will_not_finish_is_failed_and_tidying_up_cannot_hide_why()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);
        var sink = new FakeSink { PublishFails = true, DiscardFails = true };

        await camera.Controller.Download([files[0]], sink)!;

        camera.State.Library.Transfers[files[0]].ShouldBe(new Transfer.Failed("This PC could not save it: The file is in use by another process."));
        sink.Discarded.ShouldBe([files[0]]);
        camera.State.IsConnected.ShouldBeTrue();

        await camera.Controller.Download([files[0]], new FakeSink { PublishDenied = true })!;
        camera.State.Library.Transfers[files[0]].ShouldBe(new Transfer.Failed("This PC could not save it: Access to the path is denied."));
        camera.State.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task A_camera_that_will_not_leave_the_card_still_finishes_the_copy()
    {
        await using var camera = await WithCardAsync();
        var files = await ListedAsync(camera);
        var sink = new FakeSink { OnCreate = () => camera.Control.ForcedRefusals[GpSockCommand.SetMode] = NakCode.ModeError };

        await camera.Controller.Download([files[0]], sink)!;

        camera.State.Library.Transfers[files[0]].ShouldBeOfType<Transfer.Saved>();
        camera.Control.Mode.ShouldBe(CameraMode.Browse);
        camera.State.IsConnected.ShouldBeTrue();
    }
}
