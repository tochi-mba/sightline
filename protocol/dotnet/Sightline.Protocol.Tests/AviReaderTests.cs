using System.Text;
using Shouldly;
using Sightline.Protocol.Media;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// Reading a clip as it comes off the card, against AVIs built here in the reference camera's shape: Motion
/// JPEG in <c>00dc</c> chunks and 16 kHz mono PCM in <c>01wb</c>, a stream header each, an index at the end.
/// </summary>
/// <remarks>
/// Synthetic, like every fixture here: a real clip shows somebody's room. One was read on 2026-10-07 for its
/// shape: 1920×1080 at 30 frames a second, 149 pictures, 9 runs of sound.
/// </remarks>
public sealed class AviReaderTests
{
    private static readonly byte[] First = Jpeg(301, 0x11);
    private static readonly byte[] Second = Jpeg(240, 0x22);
    private static readonly byte[] Noise = [1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public void A_clip_read_whole_gives_its_headers_then_its_pictures_and_sound_in_order()
    {
        var reader = new AviReader();

        var chunks = reader.Push(FakeClip.Reference([First, Second], [Noise]).Build());

        reader.Clip.ShouldBe(new AviClip(1920, 1080, 30, 2, new AviSound(16000, 1, 16)));
        chunks.Count.ShouldBe(3);
        chunks[0].ShouldBeOfType<AviChunk.Picture>().Jpeg.ShouldBe(First);
        chunks[1].ShouldBeOfType<AviChunk.Sound>().Pcm.ShouldBe(Noise);
        var second = chunks[2].ShouldBeOfType<AviChunk.Picture>();
        second.Number.ShouldBe(1);
        second.Jpeg.ShouldBe(Second);
    }

    [Fact]
    public void Each_piece_says_where_in_the_file_its_bytes_lie()
    {
        var file = FakeClip.Reference([First, Second], [Noise]).Build();

        var chunks = new AviReader().Push(file);

        var picture = chunks[0].ShouldBeOfType<AviChunk.Picture>();
        file.AsSpan((int)picture.Offset, picture.Jpeg.Length).ToArray().ShouldBe(First);
        var sound = chunks[1].ShouldBeOfType<AviChunk.Sound>();
        file.AsSpan((int)sound.Offset, sound.Pcm.Length).ToArray().ShouldBe(Noise);
        var second = chunks[2].ShouldBeOfType<AviChunk.Picture>();
        file.AsSpan((int)second.Offset, second.Jpeg.Length).ToArray().ShouldBe(Second);
    }

    [Fact]
    public void A_clip_arriving_a_byte_at_a_time_reads_the_same()
    {
        var file = FakeClip.Reference([First, Second], [Noise]).Build();
        var reader = new AviReader();
        var chunks = new List<AviChunk>();

        foreach (var b in file)
        {
            chunks.AddRange(reader.Push([b]));
        }

        reader.Clip!.Width.ShouldBe(1920);
        chunks.OfType<AviChunk.Picture>().Select(p => p.Jpeg.Length).ShouldBe([301, 240]);
        chunks.OfType<AviChunk.Sound>().Single().Pcm.ShouldBe(Noise);
    }

    [Fact]
    public void Pieces_of_every_size_read_the_same_as_the_whole()
    {
        var file = FakeClip.Reference([First, Second, First], [Noise, Noise]).Build();
        foreach (var size in new[] { 2, 7, 13, 64, 500 })
        {
            var reader = new AviReader();
            var chunks = new List<AviChunk>();
            for (var at = 0; at < file.Length; at += size)
            {
                chunks.AddRange(reader.Push(file.AsSpan(at, Math.Min(size, file.Length - at))));
            }

            chunks.Count.ShouldBe(5, $"in pieces of {size}");
        }
    }

    [Fact]
    public void How_long_a_clip_plays_and_when_each_picture_shows_come_from_its_rate()
    {
        var clip = new AviClip(1920, 1080, 30, 150, null);

        clip.Duration.ShouldBe(TimeSpan.FromSeconds(5));
        clip.TimeOf(45).ShouldBe(TimeSpan.FromSeconds(1.5));
        new AviClip(640, 360, 0, 10, null).Duration.ShouldBe(TimeSpan.Zero);
        new AviClip(640, 360, 0, 10, null).TimeOf(3).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void The_video_streams_own_rate_wins_over_the_main_headers()
    {
        // 29.97 frames a second is 30000/1001 in the stream header; the main header rounds it to 33367 us.
        var avi = FakeClip.Reference([First], []) with { MicrosPerFrame = 33367, Scale = 1001, Rate = 30000 };

        var reader = new AviReader();
        reader.Push(avi.Build());

        reader.Clip!.FramesPerSecond.ShouldBe(30000.0 / 1001);
    }

    [Fact]
    public void A_stream_header_with_no_rate_leaves_the_main_headers_and_no_rate_at_all_reads_as_zero()
    {
        var reader = new AviReader();
        reader.Push((FakeClip.Reference([First], []) with { Scale = 0 }).Build());
        reader.Clip!.FramesPerSecond.ShouldBe(30, 0.001);

        var none = new AviReader();
        none.Push((FakeClip.Reference([First], []) with { MicrosPerFrame = 0, Rate = 0 }).Build());
        none.Clip!.FramesPerSecond.ShouldBe(0);
    }

    [Fact]
    public void An_odd_length_picture_is_read_without_its_pad_byte()
    {
        var odd = Jpeg(301, 0x33);
        var reader = new AviReader();

        var chunks = reader.Push(FakeClip.Reference([odd, Second], []).Build());

        chunks[0].ShouldBeOfType<AviChunk.Picture>().Jpeg.ShouldBe(odd);
        chunks[1].ShouldBeOfType<AviChunk.Picture>().Jpeg.ShouldBe(Second);
    }

    [Fact]
    public void A_picture_comes_out_without_the_zeros_the_camera_pads_it_with()
    {
        // As in its stream: up to seven zeros after FF D9, to a multiple of eight bytes.
        var reader = new AviReader();

        var chunks = reader.Push(FakeClip.Reference([[.. First, 0, 0, 0]], []).Build());

        chunks.ShouldHaveSingleItem().ShouldBeOfType<AviChunk.Picture>().Jpeg.ShouldBe(First);
    }

    [Fact]
    public void Grouped_chunks_inside_movi_are_read_as_if_they_were_not_grouped()
    {
        var reader = new AviReader();

        var chunks = reader.Push((FakeClip.Reference([First, Second], [Noise]) with { Grouped = true }).Build());

        chunks.Count.ShouldBe(3);
    }

    [Fact]
    public void Chunks_of_streams_it_does_not_know_and_lists_it_does_not_read_are_passed_over()
    {
        var avi = FakeClip.Reference([First], [Noise]) with
        {
            Extra = [("02dc", Jpeg(50, 0x44)), ("00wb", Noise), ("01dc", Jpeg(60, 0x55)), ("JUNK", new byte[100_000])],
        };
        var reader = new AviReader();
        var chunks = new List<AviChunk>();
        var file = avi.Build();

        for (var at = 0; at < file.Length; at += 4096)
        {
            chunks.AddRange(reader.Push(file.AsSpan(at, Math.Min(4096, file.Length - at))));
        }

        chunks.OfType<AviChunk.Picture>().Single().Jpeg.ShouldBe(First);
        chunks.OfType<AviChunk.Sound>().Single().Pcm.ShouldBe(Noise);
    }

    [Fact]
    public void Sound_that_is_not_plain_pcm_is_not_offered_and_its_chunks_are_passed_over()
    {
        var reader = new AviReader();

        var chunks = reader.Push((FakeClip.Reference([First], [Noise]) with { SoundFormat = 0x55 }).Build());

        reader.Clip!.Sound.ShouldBeNull();
        chunks.ShouldHaveSingleItem().ShouldBeOfType<AviChunk.Picture>();
    }

    [Fact]
    public void Headers_too_short_to_hold_what_they_should_are_ignored_rather_than_misread()
    {
        var reader = new AviReader();

        var chunks = reader.Push((FakeClip.Reference([First], [Noise]) with { Truncated = true }).Build());

        reader.Clip.ShouldBe(new AviClip(0, 0, 0, 0, null));
        chunks.ShouldBeEmpty();
    }

    [Fact]
    public void A_header_chunk_claiming_more_than_its_list_is_cut_off_at_the_list()
    {
        var reader = new AviReader();

        reader.Push((FakeClip.Reference([First], []) with { Overlong = true }).Build());

        reader.Clip!.Width.ShouldBe(1920);
    }

    [Fact]
    public void A_file_that_is_not_an_avi_is_refused_at_its_first_twelve_bytes()
    {
        var reader = new AviReader();
        reader.Push(Encoding.ASCII.GetBytes("RIFF")).ShouldBeEmpty();

        Should.Throw<InvalidDataException>(() => reader.Push(Encoding.ASCII.GetBytes("\0\0\0\0WAVE")));
        Should.Throw<InvalidDataException>(() => new AviReader().Push(Jpeg(40, 0)));
    }

    [Fact]
    public void A_picture_claiming_more_than_a_clip_holds_is_refused()
    {
        var file = new List<byte>(FakeClip.Reference([], []).Build());
        file.AddRange(Encoding.ASCII.GetBytes("00dc"));
        file.AddRange(BitConverter.GetBytes(64 * 1024 * 1024));

        Should.Throw<InvalidDataException>(() => new AviReader().Push(file.ToArray()))
            .Message.ShouldContain("more than a clip of this camera holds");
    }

    private static byte[] Jpeg(int length, byte fill)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }
}
