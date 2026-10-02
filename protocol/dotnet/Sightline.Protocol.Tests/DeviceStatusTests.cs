using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The device status, against the bytes the reference camera actually sent.
/// </summary>
/// <remarks>
/// The reference payload is <c>000280010025b3000000fe7c0000a501</c>: sixteen bytes, read on
/// 2026-10-02 while the camera sat in record mode on USB power. The vendor documentation for a
/// later firmware describes twenty bytes with a different layout, so the fields past the first few
/// are deliberately not decoded — see <see cref="DeviceStatus"/>.
/// </remarks>
public sealed class DeviceStatusTests
{
    private static byte[] Reference() =>
        Convert.FromHexString(
            File.ReadAllText(Path.Combine("golden", "gpsock", "device-status-16byte.hex")).Trim());

    [Fact]
    public void The_reference_camera_answers_with_sixteen_bytes_not_the_documented_twenty()
    {
        new DeviceStatus(Reference()).Length.ShouldBe(16);
    }

    [Fact]
    public void It_reads_the_mode_the_camera_was_actually_in()
    {
        new DeviceStatus(Reference()).Mode.ShouldBe(CameraMode.Record);
    }

    [Fact]
    public void It_reads_that_the_camera_was_on_external_power()
    {
        // The camera was plugged into USB when this was captured.
        new DeviceStatus(Reference()).IsCharging.ShouldBeTrue();
    }

    [Fact]
    public void It_reads_that_the_camera_was_not_recording()
    {
        new DeviceStatus(Reference()).IsBusy.ShouldBeFalse();
    }

    [Fact]
    public void Battery_is_reported_as_unknown_rather_than_as_a_misread_byte()
    {
        // The documented layout puts a level in byte 2, but this firmware reports 0x80 there on
        // mains power, which is not a percentage. Showing nothing beats showing "128%".
        new DeviceStatus(Reference()).BatteryPercent.ShouldBeNull();
    }

    [Fact]
    public void Free_space_is_reported_as_unknown_because_this_payload_does_not_reach_it()
    {
        new DeviceStatus(Reference()).FreeSpaceBytes.ShouldBeNull();
    }

    [Fact]
    public void Every_byte_stays_available_so_diagnostics_can_show_what_is_not_understood()
    {
        var status = new DeviceStatus(Reference());

        status.Raw.ToArray().ShouldBe(Reference());
        status.Describe().ShouldContain("000280010025B3000000FE7C0000A501");
    }

    [Fact]
    public void A_payload_too_short_to_hold_a_mode_is_refused()
    {
        Should.Throw<GpSockProtocolException>(() => new DeviceStatus([0x00, 0x02]));
    }

    [Fact]
    public void A_camera_in_browse_mode_reads_as_browse()
    {
        new DeviceStatus([(byte)CameraMode.Browse, 0, 0, 0]).Mode.ShouldBe(CameraMode.Browse);
    }

    [Fact]
    public void The_busy_and_audio_flags_are_read_independently()
    {
        new DeviceStatus([0, 0b01, 0, 0]).IsBusy.ShouldBeTrue();
        new DeviceStatus([0, 0b01, 0, 0]).RecordsAudio.ShouldBeFalse();
        new DeviceStatus([0, 0b10, 0, 0]).RecordsAudio.ShouldBeTrue();
        new DeviceStatus([0, 0b10, 0, 0]).IsBusy.ShouldBeFalse();
    }
}
