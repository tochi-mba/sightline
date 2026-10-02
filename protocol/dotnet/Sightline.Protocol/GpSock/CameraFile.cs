using System.Buffers.Binary;
using System.Globalization;

namespace Sightline.Protocol.GpSock;

/// <summary>What kind of file the camera says a card entry is.</summary>
public enum CameraFileKind
{
    /// <summary>A photograph.</summary>
    Photo,

    /// <summary>A video.</summary>
    Video,

    /// <summary>A video the camera has protected from loop recording.</summary>
    ProtectedVideo,

    /// <summary>A video the camera saved after a jolt or an emergency press.</summary>
    EmergencyVideo,

    /// <summary>Something this project does not recognise; listed rather than hidden.</summary>
    Other,
}

/// <summary>
/// One file on the camera's card, as <see cref="GpSockCommand.PlaybackGetFileList"/> describes it.
/// </summary>
/// <remarks>
/// <para>
/// Each entry on the wire is at least 13 bytes: a type letter, a 16-bit little-endian index, the
/// time it was taken as six bytes (year since 2000, month, day, hour, minute, second), and a 32-bit
/// little-endian size in kilobytes. The index is what every other playback command takes.
/// </para>
/// <para>
/// The time is the camera's own clock, which is only as right as somebody last set it — the
/// reference camera was two years out until Sightline synced it — and an impossible date is
/// reported as unknown rather than invented.
/// </para>
/// </remarks>
/// <param name="Code">The type letter the camera sent, such as <c>J</c> for a photograph.</param>
/// <param name="Index">The camera's index for the file, used by every other playback command.</param>
/// <param name="Taken">When the camera's clock says it was taken, or null when that is not a real date.</param>
/// <param name="SizeKilobytes">The size the camera reports, in kilobytes.</param>
public sealed record CameraFile(char Code, int Index, DateTime? Taken, long SizeKilobytes)
{
    /// <summary>The fewest bytes a list entry can have.</summary>
    public const int MinimumEntryLength = 13;

    /// <summary>What kind of file this is.</summary>
    public CameraFileKind Kind => Code switch
    {
        'J' => CameraFileKind.Photo,
        'A' or 'V' => CameraFileKind.Video,
        'L' or 'K' => CameraFileKind.ProtectedVideo,
        'S' or 'O' => CameraFileKind.EmergencyVideo,
        _ => CameraFileKind.Other,
    };

    /// <summary>Whether this is a photograph.</summary>
    public bool IsPhoto => Kind == CameraFileKind.Photo;

    /// <summary>Whether this is a video of any kind.</summary>
    public bool IsVideo => Kind is CameraFileKind.Video or CameraFileKind.ProtectedVideo or CameraFileKind.EmergencyVideo;

    /// <summary>The size in bytes, as near as the camera's kilobyte figure allows.</summary>
    public long ApproximateBytes => SizeKilobytes * 1024;

    /// <summary>
    /// A name for the file, in the camera's own style.
    /// </summary>
    /// <remarks>
    /// The list carries no name, so this is built from the kind and the index. The extension is
    /// settled when the file is downloaded, from what its bytes actually are.
    /// </remarks>
    public string DisplayName => Kind switch
    {
        CameraFileKind.Photo => $"PICT{Index:D4}",
        CameraFileKind.ProtectedVideo => $"LOCK{Index:D4}",
        CameraFileKind.EmergencyVideo => $"SOS{Index:D4}",
        CameraFileKind.Video => $"MOVI{Index:D4}",
        _ => $"FILE{Index:D4}",
    };

    /// <summary>
    /// Reads one page of the file list.
    /// </summary>
    /// <param name="payload">The answer: a count, then that many equal-sized entries.</param>
    /// <exception cref="GpSockProtocolException">The page does not divide into whole entries.</exception>
    public static IReadOnlyList<CameraFile> ParsePage(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] == 0)
        {
            return [];
        }

        int count = payload[0];
        var bytes = payload.Length - 1;
        if (bytes % count != 0 || bytes / count < MinimumEntryLength)
        {
            throw new GpSockProtocolException(
                $"A file-list page of {count} entries cannot be {bytes} bytes long.");
        }

        var size = bytes / count;
        var files = new List<CameraFile>(count);
        for (var i = 0; i < count; i++)
        {
            files.Add(ParseEntry(payload.Slice(1 + (i * size), size)));
        }

        return files;
    }

    private static CameraFile ParseEntry(ReadOnlySpan<byte> entry) => new(
        (char)entry[0],
        BinaryPrimitives.ReadUInt16LittleEndian(entry[1..]),
        TimeFrom(entry.Slice(3, 6)),
        BinaryPrimitives.ReadUInt32LittleEndian(entry[9..]));

    private static DateTime? TimeFrom(ReadOnlySpan<byte> time)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{2000 + time[0]:D4}-{time[1]:D2}-{time[2]:D2} {time[3]:D2}:{time[4]:D2}:{time[5]:D2}");
        return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var taken)
            ? taken
            : null;
    }
}
