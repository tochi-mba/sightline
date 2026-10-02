using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The device status, against the bytes the reference camera actually sent.
/// </summary>
/// <remarks>
/// <c>device-status-16byte.hex</c> is the first payload read, in record mode on USB power.
/// <c>device-status-sequence.txt</c> is a run of payloads read while a clip was recorded and a photo
/// taken, which is what pins the decoded fields down; its header says what moved and when.
/// </remarks>
public sealed class DeviceStatusTests
{
    private static byte[] Reference() =>
        Convert.FromHexString(
            File.ReadAllText(Path.Combine("golden", "gpsock", "device-status-16byte.hex")).Trim());

    private static DeviceStatus Captured(string label)
    {
        foreach (var line in File.ReadAllLines(Path.Combine("golden", "gpsock", "device-status-sequence.txt")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == label)
            {
                return new DeviceStatus(Convert.FromHexString(parts[1]));
            }
        }

        throw new InvalidOperationException($"No captured status called {label}.");
    }

    [Fact]
    public void The_reference_camera_answers_with_sixteen_bytes_not_the_documented_twenty()
    {
        new DeviceStatus(Reference()).Length.ShouldBe(16);
    }

    [Fact]
    public void It_reads_the_mode_the_camera_was_actually_in()
    {
        new DeviceStatus(Reference()).Mode.ShouldBe(CameraMode.Record);
        Captured("capture-idle").Mode.ShouldBe(CameraMode.Capture);
    }

    [Fact]
    public void It_reads_that_the_camera_was_on_external_power()
    {
        // The camera was plugged into USB throughout.
        new DeviceStatus(Reference()).OnExternalPower.ShouldBeTrue();
        new DeviceStatus([0, 0, 0, 0]).OnExternalPower.ShouldBeFalse();
    }

    [Fact]
    public void Recording_is_read_from_the_busy_flag_in_record_mode()
    {
        Captured("record-idle").IsRecording.ShouldBeFalse();
        Captured("recording-1s").IsRecording.ShouldBeTrue();
        Captured("record-stopped").IsRecording.ShouldBeFalse();
    }

    [Fact]
    public void Busy_in_browse_mode_is_playback_not_recording()
    {
        var playingBack = new DeviceStatus([(byte)CameraMode.Browse, 0b01, 0, 0]);

        playingBack.IsBusy.ShouldBeTrue();
        playingBack.IsRecording.ShouldBeFalse();
    }

    [Fact]
    public void While_recording_the_clock_counts_the_clip()
    {
        Captured("recording-1s").ClipLength.ShouldBe(TimeSpan.FromSeconds(1));
        Captured("recording-3s").ClipLength.ShouldBe(TimeSpan.FromSeconds(3));
        Captured("recording-3s").RecordTimeLeft.ShouldBeNull();
    }

    [Fact]
    public void When_idle_the_same_bytes_count_the_video_the_card_still_holds()
    {
        // 45,861 seconds before a four-second clip, 45,857 after it.
        Captured("record-idle").RecordTimeLeft.ShouldBe(TimeSpan.FromSeconds(45_861));
        Captured("record-stopped").RecordTimeLeft.ShouldBe(TimeSpan.FromSeconds(45_857));
        Captured("record-idle").ClipLength.ShouldBeNull();
    }

    [Fact]
    public void Photos_left_drops_by_one_when_a_photo_is_taken()
    {
        Captured("capture-idle").PhotosLeft.ShouldBe(31_997);
        Captured("capture-after-photo").PhotosLeft.ShouldBe(31_996);
    }

    [Fact]
    public void The_resolutions_match_the_settings_they_mirror()
    {
        var status = Captured("record-idle");

        status.RecordResolution.ShouldBe(0);
        status.PhotoResolution.ShouldBe(0);
    }

    [Fact]
    public void A_payload_that_stops_short_reports_its_missing_fields_as_unknown()
    {
        var shortest = new DeviceStatus([0, 0, 0x80, 1]);

        shortest.RecordResolution.ShouldBeNull();
        shortest.PhotoResolution.ShouldBeNull();
        shortest.RecordTimeLeft.ShouldBeNull();
        shortest.PhotosLeft.ShouldBeNull();
    }

    [Fact]
    public void A_negative_count_is_unknown_rather_than_shown()
    {
        // Bytes 5..8 and 10..13 both 0x80000000 little-endian: negative, which no count can be.
        var odd = Convert.FromHexString("00028001000000008000000000800001");

        new DeviceStatus(odd).RecordTimeLeft.ShouldBeNull();
        new DeviceStatus(odd).PhotosLeft.ShouldBeNull();
    }

    [Fact]
    public void Battery_is_reported_as_unknown_rather_than_as_a_misread_byte()
    {
        // The vendor source puts a level in byte 2, but this firmware reports 0x80 there on mains
        // power and it never moved. Showing nothing beats showing "128%".
        new DeviceStatus(Reference()).BatteryPercent.ShouldBeNull();
    }

    [Fact]
    public void Every_byte_stays_available_so_diagnostics_can_show_what_is_not_understood()
    {
        var status = new DeviceStatus(Reference());

        status.Raw.ToArray().ShouldBe(Reference());
        status.Describe().ShouldContain("000280010025B3000000FE7C0000A501");
        status.Describe().ShouldContain("recording=False");
    }

    [Fact]
    public void A_payload_too_short_to_hold_a_mode_is_refused()
    {
        Should.Throw<GpSockProtocolException>(() => new DeviceStatus([0x00, 0x02]));
        Should.Throw<ArgumentNullException>(() => new DeviceStatus(null!));
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
