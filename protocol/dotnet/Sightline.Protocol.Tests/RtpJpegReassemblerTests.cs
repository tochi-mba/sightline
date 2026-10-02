using System.Buffers.Binary;
using Shouldly;
using Sightline.Protocol.Rtp;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// Reassembly, against packets shaped exactly like the reference camera's.
/// </summary>
/// <remarks>
/// The packets here are synthetic: the real capture is a picture of somebody's room and belongs in
/// an ignored folder, not in a public repository. Their shape is taken from the real one — bare
/// RTP with no interleaved framing, ssrc <c>0x22222222</c>, payload type 26, and a complete JFIF
/// document inside the payload rather than the stripped form RFC 2435 describes.
/// </remarks>
public sealed class RtpJpegReassemblerTests
{
    private const uint Ssrc = 0x22222222;

    [Fact]
    public void One_packet_carrying_a_whole_picture_produces_that_picture()
    {
        var jpeg = FakeJpeg(600);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, Packets(jpeg, 640, 360, fragmentSize: 10_000));

        frames.Count.ShouldBe(1);
        frames[0].Jpeg.ShouldBe(jpeg);
        frames[0].Width.ShouldBe(640);
        frames[0].Height.ShouldBe(360);
    }

    [Fact]
    public void A_picture_split_across_packets_is_put_back_together_in_order()
    {
        var jpeg = FakeJpeg(3000);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, Packets(jpeg, 640, 360, fragmentSize: 700));

        frames.Count.ShouldBe(1);
        frames[0].Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void The_reference_geometry_comes_through_as_640_by_360()
    {
        // The camera reports size in units of eight pixels: 0x50 and 0x2d.
        var frames = PushAll(new RtpJpegReassembler(), Packets(FakeJpeg(400), 640, 360, 10_000));

        frames[0].Width.ShouldBe(640);
        frames[0].Height.ShouldBe(360);
    }

    [Fact]
    public void Bytes_arriving_in_awkward_pieces_still_produce_whole_pictures()
    {
        // TCP gives no frame boundaries, and this stream has no length fields at all, so the
        // reader has to cope with a packet split anywhere - including inside its header.
        var jpeg = FakeJpeg(2000);
        // A packet carries no length, so its end is only known once the next one starts. The
        // sentinel is the next packet a live camera would always be sending anyway.
        var wire = Packets(jpeg, 640, 360, fragmentSize: 500)
            .Concat(Packets(FakeJpeg(50), 640, 360, 10_000, timestamp: 99_999))
            .SelectMany(p => p)
            .ToArray();
        var reassembler = new RtpJpegReassembler();
        var frames = new List<CameraFrame>();

        for (var offset = 0; offset < wire.Length; offset += 7)
        {
            frames.AddRange(reassembler.Push(wire.AsSpan(offset, Math.Min(7, wire.Length - offset))));
        }

        // The final picture is only emitted once the next one starts, which is how the stream works.
        frames.Count.ShouldBe(1);
        frames[0].Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void Two_pictures_in_a_row_are_separated_by_the_fragment_offset_restarting()
    {
        var first = FakeJpeg(500);
        var second = FakeJpeg(700);
        var wire = Packets(first, 640, 360, 10_000, timestamp: 1000)
            .Concat(Packets(second, 640, 360, 10_000, timestamp: 8380))
            .Concat(Packets(FakeJpeg(100), 640, 360, 10_000, timestamp: 15_760))
            .SelectMany(p => p)
            .ToArray();

        var frames = new RtpJpegReassembler().Push(wire);

        frames.Count.ShouldBe(2);
        frames[0].Jpeg.ShouldBe(first);
        frames[1].Jpeg.ShouldBe(second);
        frames[0].RtpTimestamp.ShouldBe(1000u);
        frames[1].RtpTimestamp.ShouldBe(8380u);
    }

    [Fact]
    public void A_dollar_byte_inside_the_picture_does_not_derail_it()
    {
        // This is the exact trap: 0x24 is '$', which is what RTSP interleaved framing starts with.
        // A reader looking for that framing finds these and produces nonsense.
        var jpeg = FakeJpeg(900, fill: 0x24);
        var reassembler = new RtpJpegReassembler();

        var frames = PushAll(reassembler, Packets(jpeg, 640, 360, fragmentSize: 10_000));

        frames.Count.ShouldBe(1);
        frames[0].Jpeg.ShouldBe(jpeg);
    }

    [Fact]
    public void A_truncated_final_fragment_is_still_closed_so_the_file_opens()
    {
        var reassembler = new RtpJpegReassembler();
        var withoutEnd = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

        var frames = PushAll(reassembler, Packets(withoutEnd, 640, 360, 10_000));

        RtpJpegReassembler.LooksLikeJpeg(frames[0].Jpeg).ShouldBeTrue();
    }

    [Fact]
    public void Lost_packets_are_counted_rather_than_passed_off_as_a_clean_stream()
    {
        var packets = Packets(FakeJpeg(3000), 640, 360, fragmentSize: 500).ToList();
        var reassembler = new RtpJpegReassembler();

        // Drop one from the middle, as a lossy link would.
        packets.RemoveAt(2);
        foreach (var packet in packets)
        {
            reassembler.Push(packet);
        }

        reassembler.PacketsLost.ShouldBe(1);
    }

    [Fact]
    public void Packets_from_another_source_are_ignored_rather_than_mixed_in()
    {
        var mine = Packets(FakeJpeg(400), 640, 360, 10_000).Single();
        var theirs = Packets(FakeJpeg(400), 320, 240, 10_000, ssrc: 0x99999999).Single();
        var reassembler = new RtpJpegReassembler();

        reassembler.Push(mine);
        var frames = reassembler.Push(theirs.Concat(mine).ToArray());

        frames.ShouldAllBe(f => f.Width == 640);
    }

    [Fact]
    public void A_block_that_is_not_a_jpeg_is_not_called_one()
    {
        RtpJpegReassembler.LooksLikeJpeg([1, 2, 3, 4]).ShouldBeFalse();
        RtpJpegReassembler.LooksLikeJpeg([]).ShouldBeFalse();
    }

    private static List<CameraFrame> PushAll(RtpJpegReassembler reassembler, IEnumerable<byte[]> packets)
    {
        var frames = new List<CameraFrame>();
        foreach (var packet in packets)
        {
            frames.AddRange(reassembler.Push(packet));
        }

        // A picture is only known to be finished when the next one starts, so a trailing marker
        // packet is how a test gets the last one out.
        frames.AddRange(reassembler.Push(Packets(FakeJpeg(50), 640, 360, 10_000, timestamp: 99_999).Single()));
        return frames;
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

    /// <summary>Wraps a picture in RTP/JPEG packets the way the reference camera does.</summary>
    private static IEnumerable<byte[]> Packets(
        byte[] jpeg, int width, int height, int fragmentSize, uint timestamp = 1000, uint ssrc = Ssrc)
    {
        var packets = new List<byte[]>();
        ushort sequence = 0;
        for (var offset = 0; offset < jpeg.Length; offset += fragmentSize)
        {
            var size = Math.Min(fragmentSize, jpeg.Length - offset);
            var packet = new byte[12 + 8 + size];
            packet[0] = 0x80;
            packet[1] = (byte)(RtpJpegReassembler.JpegPayloadType
                | (offset + size >= jpeg.Length ? 0x80 : 0x00));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
            packet[13] = (byte)((offset >> 16) & 0xFF);
            packet[14] = (byte)((offset >> 8) & 0xFF);
            packet[15] = (byte)(offset & 0xFF);
            packet[16] = 1;
            packet[17] = 1;
            packet[18] = (byte)(width / 8);
            packet[19] = (byte)(height / 8);
            jpeg.AsSpan(offset, size).CopyTo(packet.AsSpan(20));
            packets.Add(packet);
        }

        return packets;
    }
}
