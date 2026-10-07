using Shouldly;
using Sightline.Core.Playback;
using Sightline.Protocol.GpSock;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>Clips played from the card, kept on this PC so playing one again needs no download.</summary>
public sealed class ClipCacheTests : IDisposable
{
    private static readonly DateTime Taken = new(2026, 10, 7, 9, 30, 0);
    private static readonly byte[] Clip = FakeClip.Reference([FakeRtspCamera.Jpeg(500), FakeRtspCamera.Jpeg(500)], [new byte[16_000]]).Build();

    private readonly string folder = Path.Combine(Path.GetTempPath(), "sightline-clips-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static CameraFile Video(int index, long kilobytes = 1) => new('A', index, Taken, kilobytes);

    private static string Keep(ClipCache cache, CameraFile file, byte[] bytes)
    {
        using var pending = cache.Start(file);
        pending.Output.Write(bytes);
        return pending.Keep();
    }

    [Fact]
    public async Task A_clip_grows_where_a_player_can_follow_it_and_is_kept_whole_under_its_real_name()
    {
        var cache = new ClipCache(folder);
        var file = Video(4);

        using (var pending = cache.Start(file))
        {
            pending.Output.Write(Clip.AsSpan(0, 100));
            Path.GetFileName(pending.Path).ShouldStartWith(".MOVI0004-");
            using var follower = new FileStream(pending.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            follower.Length.ShouldBe(100);
            pending.Output.Write(Clip.AsSpan(100));
            follower.Length.ShouldBe(Clip.Length);

            pending.Keep().ShouldBe(cache.PathFor(file));
        }

        Directory.GetFiles(folder).ShouldBe([cache.PathFor(file)]);
        Path.GetFileName(cache.PathFor(file)).ShouldBe("MOVI0004-20261007-093000-1k.avi");
        using var kept = cache.Open(file)!;
        (await kept.Arrival).ShouldBe(new ClipArrival.Kept(cache.PathFor(file)));
        kept.Reader.IsComplete.ShouldBeTrue();
        kept.Reader.BytesRead.ShouldBe(Clip.Length);
    }

    [Fact]
    public void Nothing_kept_means_nothing_to_open()
    {
        var cache = new ClipCache(folder);

        cache.Open(Video(1)).ShouldBeNull();
        cache.Folder.ShouldBe(folder);
        cache.Limit.ShouldBe(ClipCache.DefaultLimit);
        Path.GetFileName(cache.PathFor(new CameraFile('A', 5, null, 2))).ShouldBe("MOVI0005-undated-2k.avi");
    }

    [Fact]
    public void A_cache_needs_a_folder_and_some_room()
    {
        Should.Throw<ArgumentException>(() => new ClipCache(" "));
        Should.Throw<ArgumentOutOfRangeException>(() => new ClipCache(folder, 0));
        Should.Throw<ArgumentNullException>(() => new ClipCache(folder).Start(null!));
    }

    [Fact]
    public async Task The_clips_played_longest_ago_go_first_until_the_new_one_fits()
    {
        var cache = new ClipCache(folder, limit: (3 * Clip.Length) + 512);
        var oldest = Keep(cache, Video(1), Clip);
        var playedAgain = Keep(cache, Video(2), Clip);
        var newer = Keep(cache, Video(3), Clip);
        File.SetLastWriteTimeUtc(oldest, Taken.AddDays(-3));
        File.SetLastWriteTimeUtc(playedAgain, Taken.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, Taken.AddDays(-1));
        using (var played = cache.Open(Video(2))!)
        {
            await played.Arrival;
        }

        // A kilobyte more does not fit beside all three; beside two it does.
        using var pending = cache.Start(Video(4));

        File.Exists(oldest).ShouldBeFalse();
        File.Exists(playedAgain).ShouldBeTrue();
        File.Exists(newer).ShouldBeTrue();
        File.Exists(pending.Path).ShouldBeTrue();
    }

    [Fact]
    public void A_clip_bigger_than_the_limit_is_still_kept_alone()
    {
        var cache = new ClipCache(folder, limit: 1024);
        var kept = Keep(cache, Video(1), Clip);

        using var pending = cache.Start(Video(2, kilobytes: 5));

        File.Exists(kept).ShouldBeFalse();
        File.Exists(pending.Path).ShouldBeTrue();
    }

    [Fact]
    public void Pieces_of_fetches_that_never_finished_go_and_a_file_in_use_is_left_for_another_time()
    {
        var cache = new ClipCache(folder, limit: 1024);
        var busyClip = Keep(cache, Video(1), Clip);
        var stale = Path.Combine(folder, ".MOVI0002-stale.part");
        var busyPiece = Path.Combine(folder, ".MOVI0003-busy.part");
        File.WriteAllBytes(stale, [1, 2, 3]);
        File.WriteAllBytes(busyPiece, [1]);

        using (new FileStream(busyPiece, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(busyClip, FileMode.Open, FileAccess.Read, FileShare.None))
        using (cache.Start(Video(4)))
        {
            File.Exists(stale).ShouldBeFalse();
            File.Exists(busyPiece).ShouldBeTrue();
            File.Exists(busyClip).ShouldBeTrue();
        }
    }

    [Fact]
    public void A_discarded_piece_is_gone_or_left_for_the_next_clean_up()
    {
        var cache = new ClipCache(folder);
        using var thrown = cache.Start(Video(1));
        thrown.Output.Write([1, 2, 3]);
        thrown.Discard();
        File.Exists(thrown.Path).ShouldBeFalse();

        using var held = cache.Start(Video(2));
        using (new FileStream(held.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            held.Discard();
            File.Exists(held.Path).ShouldBeTrue();
        }

        using (cache.Start(Video(3)))
        {
            File.Exists(held.Path).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_damaged_kept_clip_is_thrown_away_so_the_next_play_fetches_it_again()
    {
        var cache = new ClipCache(folder);
        var damaged = Keep(cache, Video(1), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);

        using var clip = cache.Open(Video(1))!;

        (await clip.Arrival).ShouldBe(new ClipArrival.Failed(
            "The copy kept on this PC was damaged, so it was thrown away. Play the clip again to fetch it from the card."));
        File.Exists(damaged).ShouldBeFalse();
        cache.Open(Video(1)).ShouldBeNull();
    }

    [Fact]
    public async Task A_kept_clip_this_PC_cannot_read_says_so_and_stays()
    {
        var cache = new ClipCache(folder);
        var kept = Keep(cache, Video(1), Clip);

        ClipArrival arrival;
        using (new FileStream(kept, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var clip = cache.Open(Video(1))!;
            arrival = await clip.Arrival;
        }

        arrival.ShouldBeOfType<ClipArrival.Failed>().Reason.ShouldStartWith("The copy kept on this PC could not be read: ");
        File.Exists(kept).ShouldBeTrue();
    }

    [Fact]
    public async Task A_kept_clip_let_go_while_it_is_read_back_stops_being_read()
    {
        var cache = new ClipCache(folder);
        var kept = Keep(cache, Video(1), Clip);
        var clip = new CardClip(Video(1));
        clip.Arriving(kept);
        clip.Dispose();

        await ClipCache.ReadBackAsync(clip, kept);

        (await clip.Arrival).ShouldBe(ClipArrival.Stopped.Instance);
        clip.Reader.IsComplete.ShouldBeFalse();
        File.Exists(kept).ShouldBeTrue();
    }
}
