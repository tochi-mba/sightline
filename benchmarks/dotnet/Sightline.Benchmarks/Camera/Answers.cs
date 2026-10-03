using System.Buffers.Binary;
using Sightline.Protocol.GpSock;

namespace Sightline.Benchmarks.Camera;

/// <summary>
/// The camera's side of the control channel, as the bytes it puts on the wire.
/// </summary>
/// <remarks>
/// Built once in a benchmark's setup, so that replaying them while the clock runs costs nothing.
/// </remarks>
internal static class Answers
{
    /// <summary>The largest frame of a file's bytes the firmware sends: 60 KB.</summary>
    public const int DownloadFrameBytes = 60 * 1024;

    /// <summary>
    /// Files on one page of the card's list.
    /// </summary>
    /// <remarks>
    /// The reference camera's own page size has not been measured. Eighteen is the most 13-byte
    /// entries that fit, with their count, in one 242-byte answer, so it is the fewest round trips
    /// the firmware's buffer allows.
    /// </remarks>
    public const int FilesPerPage = 18;

    /// <summary>One acknowledgement carrying <paramref name="payload"/>.</summary>
    public static byte[] Ack(GpSockCommand command, ReadOnlySpan<byte> payload)
    {
        var frame = Frame(GpSockType.Ack, command, payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(GpSockFrame.ResponseHeaderLength));
        return frame;
    }

    /// <summary>A refusal as the firmware sends it: the reason where an answer's size goes, and nothing after.</summary>
    public static byte[] Refusal(GpSockCommand command, NakCode reason)
    {
        var frame = Frame(GpSockType.Nak, command, 0);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(12), (short)reason);
        return frame;
    }

    /// <summary>A long answer: acknowledgements of at most <paramref name="chunkBytes"/> each, then an empty one.</summary>
    public static byte[] Chunked(GpSockCommand command, ReadOnlySpan<byte> whole, int chunkBytes)
    {
        using var wire = new MemoryStream();
        for (var offset = 0; offset < whole.Length; offset += chunkBytes)
        {
            wire.Write(Ack(command, whole.Slice(offset, Math.Min(chunkBytes, whole.Length - offset))));
        }

        wire.Write(Ack(command, []));
        return wire.ToArray();
    }

    /// <summary>
    /// A card's file list, one acknowledgement per page of <see cref="FilesPerPage"/>, with the
    /// files numbered from 1 as the camera numbers them.
    /// </summary>
    public static byte[][] FileListPages(int files)
    {
        var pages = new byte[(files + FilesPerPage - 1) / FilesPerPage][];
        for (var page = 0; page < pages.Length; page++)
        {
            var first = (page * FilesPerPage) + 1;
            var count = Math.Min(FilesPerPage, files - first + 1);
            var payload = new byte[1 + (count * CameraFile.MinimumEntryLength)];
            payload[0] = (byte)count;
            for (var entry = 0; entry < count; entry++)
            {
                WriteEntry(payload.AsSpan(1 + (entry * CameraFile.MinimumEntryLength)), first + entry);
            }

            pages[page] = Ack(GpSockCommand.PlaybackGetFileList, payload);
        }

        return pages;
    }

    /// <summary>
    /// One entry: the type letter, the index, the time taken and the size in kilobytes.
    /// </summary>
    /// <remarks>
    /// Photographs and videos alternate, a minute apart, as on a card that holds both.
    /// </remarks>
    private static void WriteEntry(Span<byte> entry, int index)
    {
        var photo = index % 2 == 0;
        var taken = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Unspecified).AddMinutes(index);
        entry[0] = (byte)(photo ? 'J' : 'A');
        BinaryPrimitives.WriteUInt16LittleEndian(entry[1..], (ushort)index);
        entry[3] = (byte)(taken.Year - 2000);
        entry[4] = (byte)taken.Month;
        entry[5] = (byte)taken.Day;
        entry[6] = (byte)taken.Hour;
        entry[7] = (byte)taken.Minute;
        entry[8] = (byte)taken.Second;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[9..], photo ? 3_000u : 120_000u);
    }

    private static byte[] Frame(GpSockType type, GpSockCommand command, int payloadLength)
    {
        var frame = new byte[GpSockFrame.ResponseHeaderLength + payloadLength];
        GpSockFrame.Tag.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)type);
        // The firmware echoes the command high byte first.
        frame[10] = (byte)((ushort)command >> 8);
        frame[11] = (byte)((ushort)command & 0xFF);
        return frame;
    }
}
