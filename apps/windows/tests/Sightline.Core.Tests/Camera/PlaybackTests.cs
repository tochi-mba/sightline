using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Playback;
using Sightline.Protocol.GpSock;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>Playing a video straight off the card: fetched, read as it arrives, and kept for next time.</summary>
public sealed class PlaybackTests : IDisposable
{
    private static readonly DateTime Taken = new(2026, 10, 7, 9, 30, 0);

    private static readonly byte[] Clip = (FakeClip.Reference(
        [FakeRtspCamera.Jpeg(1500), FakeRtspCamera.Jpeg(1500), FakeRtspCamera.Jpeg(1500), FakeRtspCamera.Jpeg(1500)],
        [new byte[16_000], new byte[16_000]]) with { PicturesPerSound = 2 }).Build();

    private readonly string folder = Path.Combine(Path.GetTempPath(), "sightline-play-" + Guid.NewGuid().ToString("N"));

    private ClipCache Cache => new(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<ControllerHarness> WithCardAsync(byte[] video, ControllerTiming? timing = null)
    {
        var camera = new ControllerHarness(timing: timing);
        camera.Control.AddFile('A', Taken, video);
        camera.Control.DownloadChunk = 1000;
        await camera.ConnectedAsync();
        await camera.Controller.RefreshLibrary()!;
        return camera;
    }

    private static CameraFile Listed(ControllerHarness camera) => camera.State.Library.Files!.Single();

    [Fact]
    public async Task A_clip_is_read_as_it_arrives_kept_whole_and_the_camera_put_back()
    {
        await using var camera = await WithCardAsync(Clip);
        var video = Listed(camera);
        camera.Controller.HoldLive("window");
        await camera.UntilAsync(s => s.Live is LiveView.Playing);

        using var clip = camera.Controller.Play(video, Cache)!;

        (await clip.Arrival).ShouldBe(new ClipArrival.Kept(Cache.PathFor(video)));
        clip.Reader.IsComplete.ShouldBeTrue();
        clip.Reader.Clip!.TotalFrames.ShouldBe(4);
        await using (var played = clip.OpenRead())
        {
            played.Length.ShouldBe(Clip.Length);
        }

        File.ReadAllBytes(Cache.PathFor(video)).ShouldBe(Clip);
        await camera.UntilAsync(s => s.Task is null);
        camera.Seen.ShouldContain(s => s.Task == CameraTask.Playing);
        camera.Seen.ShouldContain(s => s.Live is LiveView.Paused);
        camera.Control.Mode.ShouldBe(CameraMode.Record);
        await ControllerHarness.EventuallyAsync(() => camera.Streams.Count >= 2);
        Directory.GetFiles(folder).ShouldBe([Cache.PathFor(video)]);
    }

    [Fact]
    public async Task A_clip_kept_from_before_plays_without_the_camera()
    {
        await using var camera = await WithCardAsync(Clip);
        var video = Listed(camera);
        using (var first = camera.Controller.Play(video, Cache)!)
        {
            await first.Arrival;
        }

        await camera.UntilAsync(s => s.Task is null);
        await camera.Controller.DisconnectAsync();

        using var again = camera.Controller.Play(video, Cache)!;

        (await again.Arrival).ShouldBe(new ClipArrival.Kept(Cache.PathFor(video)));
        again.Reader.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_the_camera_nothing_new_plays_and_while_it_is_busy_nothing_else_starts()
    {
        await using var away = new ControllerHarness();
        away.Controller.Play(new CameraFile('A', 1, Taken, 1), Cache).ShouldBeNull();
        away.State.Notice!.Text.ShouldBe("Connect to the camera first.");

        await using var camera = await WithCardAsync(Clip, ControllerHarness.Patient);
        camera.Control.DownloadStallsAfterBytes = 2000;
        var video = Listed(camera);
        using var first = camera.Controller.Play(video, Cache)!;
        camera.State.Task.ShouldBe(CameraTask.Playing);

        camera.Controller.Play(video, Cache).ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => camera.Controller.Play(video, null!));
    }

    [Fact]
    public async Task Letting_a_clip_go_part_way_stops_the_fetch_and_leaves_the_camera_ready()
    {
        await using var camera = await WithCardAsync(Clip, ControllerHarness.Patient);
        camera.Control.DownloadStallsAfterBytes = 2000;
        var video = Listed(camera);
        var clip = camera.Controller.Play(video, Cache)!;
        await ControllerHarness.EventuallyAsync(() => clip.Reader.BytesRead >= 2000);
        clip.HasBytes.ShouldBeTrue();

        clip.Dispose();

        (await clip.Arrival).ShouldBe(ClipArrival.Stopped.Instance);
        await camera.UntilAsync(s => s.Task is null);
        camera.State.IsConnected.ShouldBeTrue();
        camera.Control.Mode.ShouldBe(CameraMode.Record);
        Directory.GetFiles(folder).ShouldBeEmpty();

        // The camera was told to stop sending, so the channel is still in step for what comes next.
        await camera.Controller.RefreshLibrary()!;
        camera.State.Library.Files!.ShouldBe([video]);
        camera.State.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task A_clip_let_go_at_once_is_stopped_and_nothing_is_kept()
    {
        await using var camera = await WithCardAsync(Clip, ControllerHarness.Patient);
        camera.Control.DownloadStallsAfterBytes = 0;
        var video = Listed(camera);

        var clip = camera.Controller.Play(video, Cache)!;
        clip.Dispose();

        (await clip.Arrival).ShouldBe(ClipArrival.Stopped.Instance);
        await camera.UntilAsync(s => s.Task is null);
        camera.State.IsConnected.ShouldBeTrue();
        File.Exists(Cache.PathFor(video)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_that_is_no_clip_is_not_played_and_the_camera_is_fine()
    {
        await using var camera = await WithCardAsync(FakeRtspCamera.Jpeg(3000));

        using var clip = camera.Controller.Play(Listed(camera), Cache)!;

        (await clip.Arrival).ShouldBe(new ClipArrival.Failed("This file cannot be played. This is not an AVI file."));
        await camera.UntilAsync(s => s.Task is null);
        camera.State.IsConnected.ShouldBeTrue();
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_ends_the_clip_with_the_cameras_reason_for_its_player_to_show()
    {
        await using var camera = await WithCardAsync(Clip);

        using var clip = camera.Controller.Play(new CameraFile('A', 99, Taken, 1), Cache)!;

        (await clip.Arrival).ShouldBe(new ClipArrival.Failed("The camera said no: it does not know that command."));
        await camera.UntilAsync(s => s.Task is null);
        camera.State.IsConnected.ShouldBeTrue();
        camera.State.Notice.ShouldBeNull();
    }

    [Fact]
    public async Task Nothing_plays_from_the_card_while_the_camera_records()
    {
        await using var camera = await WithCardAsync(Clip);
        var video = Listed(camera);
        await camera.Controller.ToggleRecording()!;

        using var clip = camera.Controller.Play(video, Cache)!;

        (await clip.Arrival).ShouldBe(new ClipArrival.Failed("Stop recording to play a clip from the card."));
        camera.Control.IsRecording.ShouldBeTrue();
        File.Exists(Cache.PathFor(video)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_clip_that_stops_coming_ends_and_the_camera_is_treated_as_lost()
    {
        await using var camera = await WithCardAsync(Clip);
        camera.Control.DownloadStallsAfterBytes = 2000;

        using var clip = camera.Controller.Play(Listed(camera), Cache)!;

        (await clip.Arrival).ShouldBe(new ClipArrival.Failed("The camera stopped sending for 0 seconds."));
        await camera.UntilAsync(s => s.Connection is Connection.Reconnecting);
        Directory.GetFiles(folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_PC_that_cannot_keep_the_clip_is_no_reason_to_think_the_camera_lost()
    {
        await using var camera = await WithCardAsync(Clip);
        Directory.CreateDirectory(folder);
        var blocked = Path.Combine(folder, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "in the way");

        using var clip = camera.Controller.Play(Listed(camera), new ClipCache(blocked))!;

        (await clip.Arrival).ShouldBeOfType<ClipArrival.Failed>().Reason.ShouldStartWith("This PC could not keep the clip: ");
        await camera.UntilAsync(s => s.Task is null);
        camera.State.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task A_camera_lost_part_way_ends_the_clip()
    {
        await using var camera = await WithCardAsync(Clip, ControllerHarness.Patient);
        camera.Control.DownloadStallsAfterBytes = 2000;
        using var clip = camera.Controller.Play(Listed(camera), Cache)!;
        await ControllerHarness.EventuallyAsync(() => clip.Reader.BytesRead >= 2000);

        camera.Link.Lease.Lose();

        (await clip.Arrival).ShouldBe(new ClipArrival.Failed("The camera was lost before the whole clip arrived."));
        Directory.GetFiles(folder).ShouldBeEmpty();
    }
}
