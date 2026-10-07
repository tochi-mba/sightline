using Shouldly;
using Sightline.Core.Playback;
using Sightline.Protocol.Media;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>When each picture of a clip shows, worked out as the clip arrives, against clips shaped like the reference camera's.</summary>
public sealed class ClipTimelineTests
{
    private static readonly AviSound Mono16K = new(16000, 1, 16);
    private static readonly AviClip WithSound = new(1920, 1080, 30, 149, Mono16K);
    private static readonly AviClip Silent = new(1920, 1080, 30, 149, null);

    [Fact]
    public void Pictures_are_spread_over_the_run_of_sound_written_after_them()
    {
        var timeline = new ClipTimeline(WithSound);

        timeline.Add(Picture(0, 100));
        timeline.Add(Picture(1, 200));
        timeline.Pictures.ShouldBeEmpty();
        timeline.Add(Sound(300, 32_000));

        timeline.Pictures.ShouldBe([new TimedPicture(TimeSpan.Zero, 100, 10), new TimedPicture(TimeSpan.FromSeconds(0.5), 200, 10)]);
        timeline.Sounds.ShouldBe([new TimedSound(TimeSpan.Zero, 300, 32_000)]);
        timeline.Ready.ShouldBe(TimeSpan.FromSeconds(1));
        timeline.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void Each_run_takes_its_own_rate_as_the_reference_camera_writes_them()
    {
        // 10 pictures, then half a second of sound (16,376 bytes at 16 kHz mono), then 13 more and another.
        var timeline = new ClipTimeline(WithSound);
        var number = 0;
        for (var i = 0; i < 10; i++)
        {
            timeline.Add(Picture(number++, 0));
        }

        timeline.Add(Sound(0, 16_376));
        for (var i = 0; i < 13; i++)
        {
            timeline.Add(Picture(number++, 0));
        }

        timeline.Add(Sound(0, 16_376));

        var run = TimeSpan.FromSeconds(16_376 / 32_000.0);
        timeline.Pictures[9].At.ShouldBe(run * 9 / 10);
        timeline.Pictures[10].At.ShouldBe(run);
        timeline.Pictures[22].At.ShouldBe(run + (run * 12 / 13));
        timeline.Ready.ShouldBe(run * 2);
    }

    [Fact]
    public void Pictures_after_the_last_run_of_sound_keep_the_rate_the_clip_kept_until_then()
    {
        var timeline = new ClipTimeline(WithSound);
        for (var i = 0; i < 5; i++)
        {
            timeline.Add(Picture(i, 0));
        }

        timeline.Add(Sound(0, 48_000));
        timeline.Add(Picture(5, 500));
        timeline.Add(Picture(6, 600));

        timeline.Finish();

        timeline.Pictures[5].At.ShouldBe(TimeSpan.FromSeconds(1.5));
        timeline.Pictures[6].At.ShouldBe(TimeSpan.FromSeconds(1.8));
        timeline.Ready.ShouldBe(TimeSpan.FromSeconds(2.1));
        timeline.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Pictures_with_no_sound_before_them_take_the_headers_rate_when_the_clip_ends()
    {
        var timeline = new ClipTimeline(WithSound);
        timeline.Add(Sound(0, 32_000));
        timeline.Add(Picture(0, 0));
        timeline.Add(Picture(1, 0));

        timeline.Finish();

        timeline.Pictures[1].At.ShouldBe(TimeSpan.FromSeconds(1 + (1 / 30.0)));
    }

    [Fact]
    public void A_clip_that_ends_with_nothing_waiting_is_complete_as_it_stands()
    {
        var timeline = new ClipTimeline(WithSound);
        timeline.Add(Picture(0, 0));
        timeline.Add(Sound(0, 32_000));

        timeline.Finish();

        timeline.Ready.ShouldBe(TimeSpan.FromSeconds(1));
        timeline.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void A_clip_with_no_sound_is_timed_by_its_headers_rate_as_each_picture_arrives()
    {
        var timeline = new ClipTimeline(Silent);

        timeline.Add(Picture(0, 0));
        timeline.Add(Picture(1, 0));
        timeline.Add(Sound(0, 32_000));

        timeline.Pictures.Select(p => p.At).ShouldBe([TimeSpan.Zero, TimeSpan.FromSeconds(1 / 30.0)]);
        timeline.Sounds.ShouldBeEmpty();
        timeline.Ready.ShouldBe(TimeSpan.FromSeconds(2 / 30.0));
        timeline.Clip.ShouldBe(Silent);
    }

    [Fact]
    public void The_picture_showing_at_a_moment_is_the_last_one_due_by_then()
    {
        var timeline = new ClipTimeline(Silent);
        for (var i = 0; i < 4; i++)
        {
            timeline.Add(Picture(i, 0));
        }

        var step = TimeSpan.FromSeconds(1 / 30.0);
        timeline.PictureAt(-TimeSpan.FromMilliseconds(1)).ShouldBe(-1);
        timeline.PictureAt(TimeSpan.Zero).ShouldBe(0);
        timeline.PictureAt(step * 2).ShouldBe(2);
        timeline.PictureAt(step * 2.5).ShouldBe(2);
        timeline.PictureAt(TimeSpan.FromMinutes(1)).ShouldBe(3);
        new ClipTimeline(Silent).PictureAt(TimeSpan.Zero).ShouldBe(-1);
    }

    [Fact]
    public void A_timeline_needs_a_clip_and_its_pieces()
    {
        Should.Throw<ArgumentNullException>(() => new ClipTimeline(null!));
        Should.Throw<ArgumentNullException>(() => new ClipTimeline(Silent).Add(null!));
    }

    private static AviChunk.Picture Picture(int number, long offset) => new(number, offset, new byte[10]);

    private static AviChunk.Sound Sound(long offset, int length) => new(offset, new byte[length]);
}
