using System.Buffers.Binary;

namespace Sightline.Protocol.Rtp;

/// <summary>One complete picture from the camera.</summary>
/// <param name="Jpeg">The JPEG, ready to decode or write to a file.</param>
/// <param name="RtpTimestamp">The camera's 90 kHz clock reading for this frame.</param>
/// <param name="Width">Pixels across, as the RTP header declared.</param>
/// <param name="Height">Pixels down, as the RTP header declared.</param>
public readonly record struct CameraFrame(byte[] Jpeg, uint RtpTimestamp, int Width, int Height);

/// <summary>
/// Turns the camera's RTP packets, one datagram each, into pictures.
/// </summary>
/// <remarks>
/// <para>
/// RFC 2435 cuts each picture into fragments, each carrying its offset into the picture, and sets the
/// marker bit on the last. A picture is handed on only when every fragment of it arrived in order: UDP
/// drops a packet now and then, and a picture with a hole in it decodes as a smear, not a frame. One that
/// lost a fragment is counted in <see cref="PicturesDropped"/> and the next one is waited for.
/// </para>
/// <para>
/// <b>Each picture already carries its JFIF header.</b> RFC 2435 strips the quantisation and Huffman
/// tables from the wire and expects the receiver to rebuild them; this camera leaves a complete JFIF
/// document in the payload. So fragments are simply put together, and none of that reconstruction is
/// needed or wanted.
/// </para>
/// </remarks>
public sealed class RtpJpegReassembler
{
    /// <summary>The RTP payload type RFC 2435 assigns to JPEG.</summary>
    public const byte JpegPayloadType = 26;

    private const int RtpHeaderLength = 12;
    private const int JpegHeaderLength = 8;
    private const int RestartHeaderLength = 4;

    private readonly List<byte> picture = [];
    private uint? source;
    private ushort? lastSequence;
    private uint timestamp;
    private int width;
    private int height;
    private bool building;

    /// <summary>How many RTP/JPEG packets have been taken.</summary>
    public int PacketsRead { get; private set; }

    /// <summary>How many packets never arrived, going by the gaps in their sequence numbers.</summary>
    public int PacketsLost { get; private set; }

    /// <summary>How many pictures were thrown away because a fragment of them never arrived.</summary>
    public int PicturesDropped { get; private set; }

    /// <summary>
    /// Takes one packet, and returns the picture it finished, if it finished one.
    /// </summary>
    /// <param name="packet">One datagram as it arrived. Anything that is not an RTP/JPEG packet is ignored.</param>
    public CameraFrame? Push(ReadOnlySpan<byte> packet)
    {
        if (!TryFindPayload(packet, out var header, out var payload, out var end))
        {
            return null;
        }

        PacketsRead++;
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        if (source != ssrc)
        {
            // A new sender, or the same camera starting a new stream: nothing in hand belongs with it.
            source = ssrc;
            lastSequence = null;
            Abandon(counted: false);
        }

        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (lastSequence is { } previous)
        {
            var gap = (ushort)(sequence - previous - 1);
            if (gap is > 0 and < 0x8000)
            {
                PacketsLost += gap;
            }
        }

        lastSequence = sequence;
        var marker = (packet[1] & 0x80) != 0;
        var stamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        var jpeg = packet[header..];
        var offset = (jpeg[1] << 16) | (jpeg[2] << 8) | jpeg[3];

        if (offset == 0)
        {
            // A picture starts. One still being put together never got its last fragment.
            Abandon(counted: true);
            building = true;
            timestamp = stamp;
            width = jpeg[6] * 8;
            height = jpeg[7] * 8;
        }
        else if (!building)
        {
            // Joined part-way through a picture, or after one was abandoned: wait for the next.
            return null;
        }
        else if (offset != picture.Count || stamp != timestamp)
        {
            // A fragment before this one never arrived.
            Abandon(counted: true);
            return null;
        }

        picture.AddRange(packet[payload..end]);
        if (!marker)
        {
            return null;
        }

        var finished = new CameraFrame([.. picture], timestamp, width, height);
        picture.Clear();
        building = false;
        return finished;
    }

    /// <summary>Whether a block of bytes looks like a complete JPEG.</summary>
    public static bool LooksLikeJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 4
        && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
        && bytes[^2] == 0xFF && bytes[^1] == 0xD9;

    /// <summary>
    /// Finds where the picture's bytes lie in <paramref name="packet"/>, after the RTP header with any
    /// contributing sources and extension, the JPEG header, any restart header and any inline tables, and
    /// before any padding.
    /// </summary>
    /// <returns>False for anything that is not a well-formed RTP/JPEG packet.</returns>
    private static bool TryFindPayload(ReadOnlySpan<byte> packet, out int header, out int start, out int end)
    {
        header = start = end = 0;
        if (packet.Length < RtpHeaderLength
            || packet[0] >> 6 != 2
            || (packet[1] & 0x7F) != JpegPayloadType)
        {
            return false;
        }

        end = packet.Length;
        if ((packet[0] & 0x20) != 0)
        {
            // Padding: its last byte says how much there is.
            end -= packet[^1];
        }

        var at = RtpHeaderLength + (4 * (packet[0] & 0x0F));
        if ((packet[0] & 0x10) != 0)
        {
            if (at + 4 > end)
            {
                return false;
            }

            at += 4 + (4 * BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]));
        }

        // RFC 2435: type-specific, a 24-bit fragment offset, type, Q, then width and height in eights.
        if (at + JpegHeaderLength > end)
        {
            return false;
        }

        header = at;
        var type = packet[at + 4];
        var quality = packet[at + 5];
        var fragmentStart = packet[at + 1] == 0 && packet[at + 2] == 0 && packet[at + 3] == 0;
        at += JpegHeaderLength;
        if (type >= 64)
        {
            at += RestartHeaderLength;
        }

        if (quality >= 128 && fragmentStart)
        {
            // Tables inline. This camera sends a whole JFIF header instead, but skipping them correctly
            // costs a few lines and keeps this honest RFC 2435.
            if (at + 4 > end)
            {
                return false;
            }

            at += 4 + BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
        }

        start = at;
        return start <= end;
    }

    /// <summary>Lets go of any picture being put together, counting it as dropped when asked to.</summary>
    private void Abandon(bool counted)
    {
        if (building && counted)
        {
            PicturesDropped++;
        }

        picture.Clear();
        building = false;
    }
}
