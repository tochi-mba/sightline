using System.Buffers.Binary;

namespace Sightline.Protocol.GpSock;

/// <summary>
/// What the camera says about itself, from <see cref="GpSockCommand.GetDeviceStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the fields this project has actually pinned down are exposed as values. The reference
/// camera (firmware 20240708 V1.3) answers with <b>16</b> bytes, while the vendor source for a
/// different build writes <b>20</b>. Each field below is decoded because a sequence of real
/// payloads moved it on purpose — a clip recorded, a photo taken — and
/// <c>protocol/golden/gpsock/device-status-sequence.txt</c> holds those payloads.
/// </para>
/// <para>
/// The rest stays in <see cref="Raw"/>, every uncertain field returns <see langword="null"/>, and
/// the UI shows only what is known: showing somebody a battery percentage that is a different
/// field misread is worse than showing nothing.
/// </para>
/// </remarks>
public sealed class DeviceStatus
{
    private readonly byte[] raw;

    /// <summary>Reads a status payload.</summary>
    /// <param name="payload">The bytes the camera sent. Never copied defensively; treated as owned.</param>
    /// <exception cref="GpSockProtocolException">The payload is too short to hold even the mode.</exception>
    public DeviceStatus(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < 4)
        {
            throw new GpSockProtocolException(
                $"A device status needs at least 4 bytes; the camera sent {payload.Length}.");
        }

        raw = payload;
    }

    /// <summary>Every byte the camera sent, so diagnostics can show what is not yet understood.</summary>
    public ReadOnlySpan<byte> Raw => raw;

    /// <summary>How many bytes the camera sent. 16 on the reference camera.</summary>
    public int Length => raw.Length;

    /// <summary>Which mode the camera is in.</summary>
    public CameraMode Mode => (CameraMode)raw[0];

    /// <summary>Whether the camera is busy: recording in record mode, playing back in browse mode.</summary>
    public bool IsBusy => (raw[1] & 0x01) != 0;

    /// <summary>Whether the camera is recording to its card.</summary>
    public bool IsRecording => Mode == CameraMode.Record && IsBusy;

    /// <summary>Whether the camera will record its own audio.</summary>
    public bool RecordsAudio => (raw[1] & 0x02) != 0;

    /// <summary>
    /// Whether the camera is on external power. Set while the reference camera was on USB.
    /// </summary>
    public bool OnExternalPower => raw[3] != 0;

    /// <summary>The Record Resolution setting's value id, or null when the payload is too short.</summary>
    public int? RecordResolution => raw.Length > 4 ? raw[4] : null;

    /// <summary>The Capture Resolution setting's value id, or null when the payload is too short.</summary>
    public int? PhotoResolution => raw.Length > 9 ? raw[9] : null;

    /// <summary>
    /// How long the clip being recorded has run, or null when not recording.
    /// </summary>
    /// <remarks>Counted 1, 2, 3 seconds through a test clip on the reference camera.</remarks>
    public TimeSpan? ClipLength => IsRecording ? Seconds(5) : null;

    /// <summary>
    /// How much more video the card holds at the current resolution, or null while recording.
    /// </summary>
    /// <remarks>The same bytes as <see cref="ClipLength"/>: they count down the card when idle.</remarks>
    public TimeSpan? RecordTimeLeft => IsRecording ? null : Seconds(5);

    /// <summary>How many more photos the card holds at the current resolution, or null when not reported.</summary>
    public int? PhotosLeft => Count(10);

    /// <summary>
    /// The battery level, or <see langword="null"/> because it is not yet decoded for this firmware.
    /// </summary>
    /// <remarks>
    /// The vendor source puts a level in byte 2, but the reference camera reports <c>0x80</c> there
    /// on mains power and the byte never moved. Until it has been watched across a real discharge,
    /// this stays unknown and the UI shows no battery figure.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "An instance value that is not decoded yet. Making it static would say it " +
                        "is a property of the type rather than of this camera's answer, and the " +
                        "signature would have to change again the moment the byte is pinned down.")]
    public int? BatteryPercent => null;

    /// <summary>A line for the diagnostics page: what is known, and the bytes that are not.</summary>
    public string Describe() =>
        $"mode={Mode} recording={IsRecording} busy={IsBusy} audio={RecordsAudio} external-power={OnExternalPower} " +
        $"raw={Convert.ToHexString(raw)}";

    private TimeSpan? Seconds(int offset) => Count(offset) is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>A little-endian count, or null when the payload stops short of it or it is negative.</summary>
    private int? Count(int offset)
    {
        if (raw.Length < offset + 4)
        {
            return null;
        }

        var value = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset));
        return value < 0 ? null : value;
    }
}
