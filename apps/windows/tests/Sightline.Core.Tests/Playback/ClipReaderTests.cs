using Shouldly;
using Sightline.Core.Playback;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>A clip read and timed as it arrives, which a player asks about from another thread.</summary>
public sealed class ClipReaderTests
{
    private static readonly byte[] Picture = Jpeg(500);

    // Half a second of 16 kHz mono 16-bit sound.
    private static readonly byte[] HalfSecond = new byte[16_000];

    [Fact]
    public void Before_its_headers_arrive_a_clip_has_nothing_to_play()
    {
        var reader = new ClipReader();

        reader.Clip.ShouldBeNull();
        reader.Ready.ShouldBe(TimeSpan.Zero);
        reader.IsComplete.ShouldBeFalse();
        reader.PictureAt(TimeSpan.Zero).ShouldBeNull();
        reader.SoundFrom(TimeSpan.Zero).Slices.ShouldBeEmpty();
        reader.SoundAfter(0).Runs.ShouldBe(0);
    }

    [Fact]
    public void A_clip_in_pieces_is_timed_as_it_arrives_and_complete_once_it_has()
    {
        var file = (FakeClip.Reference([Picture, Picture, Picture, Picture], [HalfSecond, HalfSecond]) with { PicturesPerSound = 2 }).Build();
        var reader = new ClipReader();

        foreach (var piece in file.Chunk(700))
        {
            reader.Push(piece);
        }

        reader.BytesRead.ShouldBe(file.Length);
        reader.Clip!.Width.ShouldBe(1920);
        reader.Ready.ShouldBe(TimeSpan.FromSeconds(1));
        reader.IsComplete.ShouldBeFalse();
        reader.PictureAt(TimeSpan.FromSeconds(0.6))!.Value.Number.ShouldBe(2);
        file.AsSpan((int)reader.PictureAt(TimeSpan.Zero)!.Value.Picture.Offset, Picture.Length).ToArray().ShouldBe(Picture);

        reader.Finish();

        reader.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void A_file_that_ends_before_its_headers_is_not_a_clip()
    {
        var reader = new ClipReader();
        reader.Push(FakeClip.Reference([Picture], []).Build().AsSpan(0, 40));

        Should.Throw<InvalidDataException>(reader.Finish);
    }

    [Fact]
    public void Sound_from_a_moment_part_way_through_a_run_starts_at_a_whole_sample()
    {
        var file = FakeClip.Reference([Picture, Picture], [HalfSecond, HalfSecond]).Build();
        var reader = new ClipReader();
        reader.Push(file);

        // A quarter of a second and a tenth of a millisecond in: 8,003.2 bytes, which is half-way through a sample.
        var (slices, runs) = reader.SoundFrom(TimeSpan.FromSeconds(0.25) + TimeSpan.FromTicks(1_000));

        runs.ShouldBe(2);
        slices.Count.ShouldBe(2);
        slices[0].Length.ShouldBe(16_000 - 8_002);
        slices[0].At.ShouldBe(TimeSpan.FromSeconds(8_002 / 32_000.0));
        slices[1].At.ShouldBe(TimeSpan.FromSeconds(0.5));
        slices[1].Length.ShouldBe(16_000);
    }

    [Fact]
    public void Sound_already_played_is_not_given_again()
    {
        var reader = new ClipReader();
        reader.Push(FakeClip.Reference([Picture, Picture], [HalfSecond, HalfSecond]).Build());

        reader.SoundFrom(TimeSpan.FromSeconds(0.5)).Slices.ShouldHaveSingleItem().At.ShouldBe(TimeSpan.FromSeconds(0.5));
        reader.SoundFrom(TimeSpan.FromSeconds(1)).Slices.ShouldBeEmpty();
        reader.SoundAfter(1).Slices.ShouldHaveSingleItem().At.ShouldBe(TimeSpan.FromSeconds(0.5));
        reader.SoundAfter(2).Slices.ShouldBeEmpty();
    }

    [Fact]
    public void A_clip_with_no_sound_offers_none()
    {
        var reader = new ClipReader();
        reader.Push((FakeClip.Reference([Picture], [HalfSecond]) with { SoundFormat = 0x55 }).Build());

        reader.SoundFrom(TimeSpan.Zero).Slices.ShouldBeEmpty();
        reader.SoundFrom(TimeSpan.Zero).Runs.ShouldBe(0);
    }

    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)0x5A);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }
}
