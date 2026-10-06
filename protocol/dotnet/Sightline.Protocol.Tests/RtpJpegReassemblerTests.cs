using System.Buffers.Binary;
using Shouldly;
using Sightline.Protocol.Rtp;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// Reassembly, one datagram at a time, against packets shaped like the reference camera's.
/// </summary>
/// <remarks>
/// The packets here are synthetic: the real capture is a picture of somebody's room and belongs in
/// an ignored folder, not in a public repository. Their shape is taken from the real one — payload
/// type 26, a fixed synchronisation source, the marker on each picture's last fragment, and a complete
/// JFIF document inside the payload rather than the stripped form RFC 2435 describes.
/// </remarks>
public sealed class RtpJpegReassemblerTests
{
    private const uint Ssrc = 0x22222222;

    [Fact]
    public void One_packet_carrying_a_whole_picture_produces_that_picture_at_once()
    {
        var jpeg = FakeJpeg(600);
        var reassembler = new RtpJpegReassembler();

        var frame = reassembler.Push(new Rtp(jpeg, Timestamp: 7380).Build());

        frame.ShouldNotBeNull();
        frame.Value.Jpeg.ShouldBe(jpeg);
        frame.Value.Width.ShouldBe(640);
        frame.Value.Height.ShouldBe(360);
        frame.Value.RtpTimestamp.ShouldBe(7380u);
        reassembler.PacketsRead.ShouldBe(1);
    }

    [Fact]
    public void A_picture_split_across_packets_comes_out_whole_when_its_marked_last_fragment_arrives()
    {
        var jpeg = FakeJpeg(3000);
        var reassembler = new RtpJpegReassembler();
        var packets = Fragments(jpeg, 700);

        foreach (var packet in packets[..^1])
        {
            reassembler.Push(packet).ShouldBeNull();
        }

        reassembler.Push(packets[^1])!.Value.Jpeg.ShouldBe(jpeg);
        reassembler.PicturesDropped.ShouldBe(0);
    }

    [Fact]
    public void Pictures_one_after_another_each_come_out()
    {
        var first = FakeJpeg(900, 0x11);
        var second = FakeJpeg(1300, 0x22);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, [.. Fragments(first, 500, timestamp: 1000), .. Fragments(second, 500, timestamp: 8380, sequence: 2)]);

        frames.Count.ShouldBe(2);
        frames[0].Jpeg.ShouldBe(first);
        frames[1].Jpeg.ShouldBe(second);
        reassembler.PacketsLost.ShouldBe(0);
    }

    [Fact]
    public void A_picture_that_lost_a_fragment_is_dropped_and_the_next_one_still_comes()
    {
        // UDP drops a packet now and then; a picture with a hole in it decodes as a smear.
        var damaged = Fragments(FakeJpeg(2000, 0x11), 500, timestamp: 1000);
        var next = FakeJpeg(800, 0x22);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, [damaged[0], damaged[2], damaged[3], .. Fragments(next, 500, timestamp: 8380, sequence: 4)]);

        frames.ShouldHaveSingleItem().Jpeg.ShouldBe(next);
        reassembler.PicturesDropped.ShouldBe(1);
        reassembler.PacketsLost.ShouldBe(1);
    }

    [Fact]
    public void A_picture_whose_last_fragment_never_came_is_dropped_when_the_next_one_starts()
    {
        var cut = Fragments(FakeJpeg(1500, 0x11), 500, timestamp: 1000);
        var next = FakeJpeg(400, 0x22);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, [cut[0], cut[1], .. Fragments(next, 500, timestamp: 8380, sequence: 3)]);

        frames.ShouldHaveSingleItem().Jpeg.ShouldBe(next);
        reassembler.PicturesDropped.ShouldBe(1);
    }

    [Fact]
    public void Fragments_of_a_picture_joined_part_way_through_are_let_go_without_counting_a_drop()
    {
        var joinedLate = Fragments(FakeJpeg(1500, 0x11), 500, timestamp: 1000);
        var next = FakeJpeg(400, 0x22);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, [joinedLate[1], joinedLate[2], .. Fragments(next, 500, timestamp: 8380, sequence: 3)]);

        frames.ShouldHaveSingleItem().Jpeg.ShouldBe(next);
        reassembler.PicturesDropped.ShouldBe(0);
    }

    [Fact]
    public void A_fragment_stamped_for_another_picture_drops_the_one_being_put_together()
    {
        var reassembler = new RtpJpegReassembler();
        reassembler.Push(new Rtp(new byte[500], Marker: false, Timestamp: 1000).Build());

        var stray = reassembler.Push(new Rtp(new byte[100], Offset: 500, Timestamp: 2000, Sequence: 1).Build());

        stray.ShouldBeNull();
        reassembler.PicturesDropped.ShouldBe(1);
    }

    [Fact]
    public void Lost_packets_are_counted_by_the_size_of_the_gap()
    {
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 10).Build());
        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 14).Build());

        reassembler.PacketsLost.ShouldBe(3);
    }

    [Fact]
    public void A_sequence_number_wrapping_past_its_top_is_not_a_loss()
    {
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 65535).Build());
        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 0).Build());

        reassembler.PacketsLost.ShouldBe(0);
    }

    [Fact]
    public void A_packet_arriving_late_or_twice_is_not_taken_for_thousands_lost()
    {
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 20).Build());
        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 19).Build());
        reassembler.Push(new Rtp(FakeJpeg(100), Sequence: 19).Build());

        reassembler.PacketsLost.ShouldBe(0);
    }

    [Fact]
    public void A_new_sender_starts_afresh_without_its_numbering_counting_as_loss()
    {
        // The camera starting a new stream: nothing in hand belongs with it, and nothing was lost.
        var reassembler = new RtpJpegReassembler();
        reassembler.Push(new Rtp(new byte[500], Marker: false, Sequence: 900).Build());

        var jpeg = FakeJpeg(300);
        var frame = reassembler.Push(new Rtp(jpeg, Sequence: 4, Ssrc: 0x33333333).Build());

        frame!.Value.Jpeg.ShouldBe(jpeg);
        reassembler.PacketsLost.ShouldBe(0);
        reassembler.PicturesDropped.ShouldBe(0);
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x9A, 0, 1, 0, 0, 0, 1, 0x22, 0x22, 0x22 })]
    public void A_datagram_too_short_for_an_rtp_header_is_ignored(byte[] datagram)
    {
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(datagram).ShouldBeNull();
        reassembler.PacketsRead.ShouldBe(0);
    }

    [Fact]
    public void A_packet_of_another_rtp_version_is_ignored()
    {
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(new Rtp(FakeJpeg(100), Version: 1).Build()).ShouldBeNull();
        reassembler.PacketsRead.ShouldBe(0);
    }

    [Fact]
    public void A_packet_that_is_not_jpeg_such_as_the_cameras_sound_is_ignored()
    {
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(new Rtp(FakeJpeg(100), PayloadType: 97).Build()).ShouldBeNull();
        reassembler.PacketsRead.ShouldBe(0);
    }

    [Fact]
    public void Contributing_sources_are_stepped_over()
    {
        var jpeg = FakeJpeg(400);

        new RtpJpegReassembler().Push(new Rtp(jpeg, Csrcs: 2).Build())!.Value.Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void A_header_extension_is_stepped_over()
    {
        var jpeg = FakeJpeg(400);

        new RtpJpegReassembler().Push(new Rtp(jpeg, Extension: [1, 2, 3, 4, 5, 6, 7, 8]).Build())!.Value.Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void A_header_extension_cut_short_by_the_end_of_the_packet_is_ignored()
    {
        var packet = new byte[14];
        packet[0] = 0x90;
        packet[1] = RtpJpegReassembler.JpegPayloadType;

        new RtpJpegReassembler().Push(packet).ShouldBeNull();
    }

    [Fact]
    public void Padding_at_the_end_is_not_taken_for_picture()
    {
        var jpeg = FakeJpeg(400);

        new RtpJpegReassembler().Push(new Rtp(jpeg, Padding: 3).Build())!.Value.Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void A_restart_marker_header_is_stepped_over()
    {
        var jpeg = FakeJpeg(400);

        new RtpJpegReassembler().Push(new Rtp(jpeg, Type: 65, Restart: [0, 8, 0xFF, 0xFF]).Build())!.Value.Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void Inline_quantisation_tables_are_stepped_over_on_the_first_fragment_only()
    {
        // This camera sends whole JFIF headers instead, but RFC 2435 allows tables in the first packet.
        var jpeg = FakeJpeg(1000);
        var reassembler = new RtpJpegReassembler();
        reassembler.Push(new Rtp(jpeg[..600], Marker: false, Quality: 255, Tables: new byte[128]).Build()).ShouldBeNull();

        var frame = reassembler.Push(new Rtp(jpeg[600..], Offset: 600, Quality: 255, Sequence: 1).Build());

        frame!.Value.Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void A_packet_too_short_for_its_jpeg_header_is_ignored()
    {
        var packet = new Rtp([]).Build()[..19];

        new RtpJpegReassembler().Push(packet).ShouldBeNull();
    }

    [Fact]
    public void A_first_fragment_too_short_for_the_tables_it_promises_is_ignored()
    {
        var packet = new Rtp([], Quality: 200).Build();

        new RtpJpegReassembler().Push(packet).ShouldBeNull();
    }

    [Fact]
    public void Tables_claiming_more_than_the_packet_holds_are_ignored()
    {
        var packet = new Rtp([], Quality: 200, Tables: new byte[10]).Build();
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20 + 2), 500);

        new RtpJpegReassembler().Push(packet).ShouldBeNull();
    }

    [Fact]
    public void A_block_is_called_a_jpeg_only_when_it_starts_and_ends_like_one()
    {
        RtpJpegReassembler.LooksLikeJpeg(FakeJpeg(100)).ShouldBeTrue();
        RtpJpegReassembler.LooksLikeJpeg([0xFF, 0xD8, 0xFF, 0xD9]).ShouldBeFalse();
        RtpJpegReassembler.LooksLikeJpeg([0x00, 0xD8, 0xFF, 0x00, 0xFF, 0xD9]).ShouldBeFalse();
        RtpJpegReassembler.LooksLikeJpeg([0xFF, 0x00, 0xFF, 0x00, 0xFF, 0xD9]).ShouldBeFalse();
        RtpJpegReassembler.LooksLikeJpeg([0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9]).ShouldBeFalse();
        RtpJpegReassembler.LooksLikeJpeg([0xFF, 0xD8, 0xFF, 0x00, 0x00, 0xD9]).ShouldBeFalse();
        RtpJpegReassembler.LooksLikeJpeg([0xFF, 0xD8, 0xFF, 0x00, 0xFF, 0x00]).ShouldBeFalse();
    }

    private static List<CameraFrame> PushAll(RtpJpegReassembler reassembler, IEnumerable<byte[]> packets)
    {
        var frames = new List<CameraFrame>();
        foreach (var packet in packets)
        {
            if (reassembler.Push(packet) is { } frame)
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    /// <summary>A picture cut into fragments the way the reference camera cuts them, numbered from <paramref name="sequence"/>.</summary>
    private static byte[][] Fragments(byte[] jpeg, int size, uint timestamp = 1000, ushort sequence = 0)
    {
        var packets = new List<byte[]>();
        for (var offset = 0; offset < jpeg.Length; offset += size)
        {
            var end = Math.Min(offset + size, jpeg.Length);
            packets.Add(new Rtp(jpeg[offset..end], offset, end == jpeg.Length, timestamp, sequence++).Build());
        }

        return [.. packets];
    }

    /// <summary>A byte block shaped like a JPEG, which is all reassembly needs it to be.</summary>
    private static byte[] FakeJpeg(int length, byte fill = 0x5A)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }

    /// <summary>One RTP/JPEG packet with every field under the test's control, laid out as RFC 3550 and RFC 2435 say.</summary>
    private sealed record Rtp(
        byte[] Payload,
        int Offset = 0,
        bool Marker = true,
        uint Timestamp = 1000,
        ushort Sequence = 0,
        uint Ssrc = Ssrc,
        byte Type = 1,
        byte Quality = 1,
        int Csrcs = 0,
        byte[]? Extension = null,
        int Padding = 0,
        byte[]? Restart = null,
        byte[]? Tables = null,
        byte PayloadType = RtpJpegReassembler.JpegPayloadType,
        int Version = 2)
    {
        public byte[] Build()
        {
            var bytes = new List<byte>
            {
                (byte)((Version << 6) | (Padding > 0 ? 0x20 : 0) | (Extension is null ? 0 : 0x10) | Csrcs),
                (byte)(PayloadType | (Marker ? 0x80 : 0)),
            };
            bytes.AddRange(BigEndian16(Sequence));
            bytes.AddRange(BigEndian32(Timestamp));
            bytes.AddRange(BigEndian32(Ssrc));
            for (var i = 0; i < Csrcs; i++)
            {
                bytes.AddRange(BigEndian32(0x44444444));
            }

            if (Extension is not null)
            {
                bytes.AddRange(BigEndian16(0xBEDE));
                bytes.AddRange(BigEndian16((ushort)(Extension.Length / 4)));
                bytes.AddRange(Extension);
            }

            bytes.Add(0);
            bytes.Add((byte)(Offset >> 16));
            bytes.Add((byte)(Offset >> 8));
            bytes.Add((byte)Offset);
            bytes.AddRange([Type, Quality, 640 / 8, 360 / 8]);
            if (Restart is not null)
            {
                bytes.AddRange(Restart);
            }

            if (Tables is not null)
            {
                bytes.AddRange([0, 0]);
                bytes.AddRange(BigEndian16((ushort)Tables.Length));
                bytes.AddRange(Tables);
            }

            bytes.AddRange(Payload);
            for (var i = 1; i <= Padding; i++)
            {
                bytes.Add(i == Padding ? (byte)Padding : (byte)0);
            }

            return [.. bytes];
        }

        private static byte[] BigEndian16(ushort value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] BigEndian32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }
    }
}
