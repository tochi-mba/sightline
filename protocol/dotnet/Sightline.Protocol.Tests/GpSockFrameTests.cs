using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The GPSOCKET codec, against bytes the real camera produced.
/// </summary>
/// <remarks>
/// The expected request bytes here are not invented: the firmware hardcodes the acknowledgement
/// for the file-list command as the twelve bytes <c>47 50 53 4F 43 4B 45 54 02 00 03 03</c>, which
/// is what pins down the little-endian type and the split of the command into two bytes.
/// </remarks>
public sealed class GpSockFrameTests
{
    [Fact]
    public void A_request_begins_with_the_tag_that_stops_the_camera_ignoring_it()
    {
        var frame = GpSockFrame.Encode(GpSockCommand.GetDeviceStatus);

        frame[..8].ShouldBe("GPSOCKET"u8.ToArray());
    }

    [Fact]
    public void A_request_carries_the_type_little_endian_and_the_command_as_two_bytes()
    {
        var frame = GpSockFrame.Encode(GpSockCommand.GetDeviceStatus);

        frame.ShouldBe(new byte[] { 0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x01, 0x00, 0x00, 0x01 });
    }

    [Theory]
    [InlineData(GpSockCommand.CapturePicture, 0x02, 0x00)]
    [InlineData(GpSockCommand.RecordToggle, 0x01, 0x00)]
    [InlineData(GpSockCommand.PlaybackGetFileList, 0x03, 0x03)]
    [InlineData(GpSockCommand.MenuSetParameter, 0x04, 0x01)]
    public void The_command_splits_into_a_group_byte_and_a_command_byte(
        GpSockCommand command, byte group, byte within)
    {
        var frame = GpSockFrame.Encode(command);

        frame[10].ShouldBe(group);
        frame[11].ShouldBe(within);
    }

    [Fact]
    public void A_payload_follows_the_header_with_no_length_in_front_of_it()
    {
        var frame = GpSockFrame.Encode(GpSockCommand.SetMode, [(byte)CameraMode.Browse]);

        frame.Length.ShouldBe(13);
        frame[12].ShouldBe((byte)2);
    }

    [Fact]
    public void An_acknowledgement_decodes_to_its_command_and_payload()
    {
        var bytes = Response(GpSockType.Ack, GpSockCommand.PlaybackGetFileCount, [0x02, 0x00]);

        GpSockFrame.TryDecode(bytes, out var response, out var consumed).ShouldBeTrue();

        consumed.ShouldBe(bytes.Length);
        response.IsAck.ShouldBeTrue();
        response.Command.ShouldBe(GpSockCommand.PlaybackGetFileCount);
        response.Payload.ShouldBe(new byte[] { 0x02, 0x00 });
    }

    [Fact]
    public void A_refusal_carries_its_reason_where_an_acknowledgement_carries_its_size()
    {
        // Exactly what the firmware sends for "busy": gp_resp_set(NAK | cmd, -1, NULL, 0) is the
        // fourteen-byte header with 0xFFFF in the size slot and no payload at all. Read as a size,
        // that is 65,535 bytes that never arrive, and the app hangs on the commonest refusal.
        var bytes = new byte[] { 0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x03, 0x00, 0x03, 0x03, 0xFF, 0xFF };

        GpSockFrame.TryDecode(bytes, out var response, out var consumed).ShouldBeTrue();

        consumed.ShouldBe(14);
        response.IsAck.ShouldBeFalse();
        response.Command.ShouldBe(GpSockCommand.PlaybackGetFileList);
        response.Nak.ShouldBe(NakCode.ServerBusy);
    }

    [Fact]
    public void A_refusal_takes_no_bytes_from_the_frame_after_it()
    {
        var refusal = new byte[] { 0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x03, 0x00, 0x03, 0x02, 0xFB, 0xFF };
        var next = Response(GpSockType.Ack, GpSockCommand.GetDeviceStatus, [7]);
        var both = refusal.Concat(next).ToArray();

        GpSockFrame.TryDecode(both, out var first, out var consumed).ShouldBeTrue();
        first.Nak.ShouldBe(NakCode.NoStorage);

        GpSockFrame.TryDecode(both.AsSpan(consumed), out var second, out _).ShouldBeTrue();
        second.Command.ShouldBe(GpSockCommand.GetDeviceStatus);
        second.Payload.ShouldBe(new byte[] { 7 });
    }

    [Fact]
    public void A_frame_split_across_two_reads_is_not_an_error_it_is_just_not_ready()
    {
        var bytes = Response(GpSockType.Ack, GpSockCommand.GetParameterFile, [1, 2, 3, 4]);

        // Every prefix short of the whole frame must report "not yet" rather than throwing or
        // inventing a frame, because this is exactly what a TCP read gives you.
        for (var length = 0; length < bytes.Length; length++)
        {
            GpSockFrame.TryDecode(bytes.AsSpan(0, length), out _, out var consumed).ShouldBeFalse();
            consumed.ShouldBe(0);
        }

        GpSockFrame.TryDecode(bytes, out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void Two_frames_in_one_read_are_taken_one_at_a_time()
    {
        var first = Response(GpSockType.Ack, GpSockCommand.GetDeviceStatus, [9]);
        var second = Response(GpSockType.Ack, GpSockCommand.PlaybackGetFileCount, [2, 0]);
        var both = first.Concat(second).ToArray();

        GpSockFrame.TryDecode(both, out var one, out var consumed).ShouldBeTrue();
        one.Command.ShouldBe(GpSockCommand.GetDeviceStatus);

        GpSockFrame.TryDecode(both.AsSpan(consumed), out var two, out _).ShouldBeTrue();
        two.Command.ShouldBe(GpSockCommand.PlaybackGetFileCount);
    }

    [Fact]
    public void Bytes_that_are_not_a_frame_are_reported_rather_than_resynchronised_past()
    {
        // There is no framing to resynchronise to, so quietly skipping ahead would turn a
        // desynchronised stream into silently wrong answers.
        var rubbish = new byte[20];

        Should.Throw<GpSockProtocolException>(
            () => GpSockFrame.TryDecode(rubbish, out _, out _));
    }

    [Fact]
    public void An_empty_acknowledgement_is_how_a_chunked_answer_ends()
    {
        var bytes = Response(GpSockType.Ack, GpSockCommand.GetParameterFile, []);

        GpSockFrame.TryDecode(bytes, out var response, out _).ShouldBeTrue();

        response.IsEndOfChunks.ShouldBeTrue();
    }

    [Fact]
    public void A_refusal_is_never_mistaken_for_the_end_of_a_chunked_answer()
    {
        var bytes = new byte[] { 0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x03, 0x00, 0x00, 0x02, 0x00, 0x00 };

        GpSockFrame.TryDecode(bytes, out var response, out _).ShouldBeTrue();

        response.IsEndOfChunks.ShouldBeFalse();
    }

    private static byte[] Response(GpSockType type, GpSockCommand command, byte[] payload)
    {
        var frame = new byte[GpSockFrame.ResponseHeaderLength + payload.Length];
        "GPSOCKET"u8.CopyTo(frame);
        frame[8] = (byte)((ushort)type & 0xFF);
        frame[9] = (byte)((ushort)type >> 8);
        frame[10] = (byte)((ushort)command >> 8);
        frame[11] = (byte)((ushort)command & 0xFF);
        frame[12] = (byte)(payload.Length & 0xFF);
        frame[13] = (byte)(payload.Length >> 8);
        payload.CopyTo(frame, GpSockFrame.ResponseHeaderLength);
        return frame;
    }
}
