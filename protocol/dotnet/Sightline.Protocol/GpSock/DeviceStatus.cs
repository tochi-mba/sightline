namespace Sightline.Protocol.GpSock;

/// <summary>
/// What the camera says about itself, from <see cref="GpSockCommand.GetDeviceStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the fields this project has actually pinned down are exposed as values. The reference
/// camera (firmware 20240708 V1.3) answers with <b>16</b> bytes, while the vendor documentation
/// for a later build describes <b>20</b>, and the two do not agree past the first few fields.
/// Decoding the rest from that documentation would mean showing somebody a battery percentage or
/// a free-space figure that is simply a different field misread — worse than showing nothing.
/// </para>
/// <para>
/// So the undecoded bytes stay in <see cref="Raw"/>, every uncertain field is nullable and returns
/// <see langword="null"/>, and the UI shows only what is known. Each field is promoted out of
/// "unknown" by an experiment that moves it on purpose — charge the camera, pull the card, fill
/// the storage — and the test that pins it down carries the observation that justified it.
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

    /// <summary>Which mode the camera is in. Confirmed: the camera reported record while recording.</summary>
    public CameraMode Mode => (CameraMode)raw[0];

    /// <summary>Whether the camera is busy recording or playing back.</summary>
    public bool IsBusy => (raw[1] & 0x01) != 0;

    /// <summary>Whether the camera will record its own audio.</summary>
    public bool RecordsAudio => (raw[1] & 0x02) != 0;

    /// <summary>
    /// Whether the camera is on external power. Confirmed: set while the reference camera was on USB.
    /// </summary>
    public bool IsCharging => raw[3] != 0;

    /// <summary>
    /// The battery level, or <see langword="null"/> because it is not yet decoded for this firmware.
    /// </summary>
    /// <remarks>
    /// The documented 20-byte layout puts a level in byte 2, but the reference camera reports
    /// <c>0x80</c> there while on mains power, which reads as either "128" on a 0-100 scale or as a
    /// flag bit rather than a level. Until the byte has been watched across a real discharge, this
    /// stays unknown and the UI shows no battery figure.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "An instance value that is not decoded yet. Making it static would say it " +
                        "is a property of the type rather than of this camera's answer, and the " +
                        "signature would have to change again the moment the byte is pinned down.")]
    public int? BatteryPercent => null;

    /// <summary>
    /// Free space on the card, or <see langword="null"/> because it is not yet decoded.
    /// </summary>
    /// <remarks>
    /// The 20-byte layout puts a 32-bit count at offset 16, which this 16-byte payload does not
    /// reach. The figure must come from a build whose layout is known, or from an experiment that
    /// fills the card by a known amount.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "An instance value that is not decoded yet; see BatteryPercent.")]
    public long? FreeSpaceBytes => null;

    /// <summary>A line for the diagnostics page: what is known, and the bytes that are not.</summary>
    public string Describe() =>
        $"mode={Mode} busy={IsBusy} audio={RecordsAudio} charging={IsCharging} " +
        $"raw={Convert.ToHexString(raw)}";
}
