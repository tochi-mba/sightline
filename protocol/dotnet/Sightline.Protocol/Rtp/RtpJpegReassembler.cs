using System.Buffers.Binary;

namespace Sightline.Protocol.Rtp;

/// <summary>One complete picture from the camera.</summary>
/// <param name="Jpeg">The JPEG, ready to decode or write to a file.</param>
/// <param name="RtpTimestamp">The camera's 90 kHz clock reading for this frame.</param>
/// <param name="Width">Pixels across, as the RTP header declared.</param>
/// <param name="Height">Pixels down, as the RTP header declared.</param>
public readonly record struct CameraFrame(byte[] Jpeg, uint RtpTimestamp, int Width, int Height);

/// <summary>
/// Turns the camera's stream into pictures.
/// </summary>
/// <remarks>
/// <para>
/// Two things about this camera make a stock RTSP stack the wrong tool, and both are why this
/// class exists.
/// </para>
/// <para>
/// <b>There is no interleaved framing.</b> The camera answers a TCP transport request with
/// <c>interleaved=0-1</c> and then sends bare RTP packets down the connection, with none of the
/// <c>$</c>, channel and length bytes that RFC 2326 requires. A reader that looks for that framing
/// finds <c>$</c> bytes at random points inside JPEG data and produces nonsense. Packets are
/// instead split on the RTP header itself, anchored to the stream's own synchronisation source.
/// </para>
/// <para>
/// <b>Each frame already carries its JFIF header.</b> RFC 2435 strips the quantisation and Huffman
/// tables from the wire and expects the receiver to rebuild them; this camera leaves a complete
/// JFIF document in the payload. So fragments are simply concatenated, and none of that
/// reconstruction is needed or wanted.
/// </para>
/// </remarks>
public sealed class RtpJpegReassembler
{
    /// <summary>The RTP payload type RFC 2435 assigns to JPEG.</summary>
    public const byte JpegPayloadType = 26;

    private const int RtpHeaderLength = 12;
    private const int JpegHeaderLength = 8;

    private readonly List<byte> stream = [];
    private readonly List<byte> frame = [];
    private uint? synchronisationSource;
    private uint currentTimestamp;
    private int width;
    private int height;
    private bool building;

    /// <summary>How many packets have been read.</summary>
    public int PacketsRead { get; private set; }

    /// <summary>How many packets were dropped because the sequence number jumped.</summary>
    public int PacketsLost { get; private set; }

    private ushort? lastSequence;

    /// <summary>
    /// Adds bytes from the connection and returns any pictures they completed.
    /// </summary>
    /// <param name="bytes">Whatever the last read produced; boundaries do not matter.</param>
    public IReadOnlyList<CameraFrame> Push(ReadOnlySpan<byte> bytes)
    {
        stream.AddRange(bytes);
        var finished = new List<CameraFrame>();

        while (true)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stream);
            var start = FindPacketStart(span, 0);
            if (start < 0)
            {
                // Nothing usable yet. Keep a little context so a header split across two reads is
                // still found, and drop the rest so a desynchronised stream cannot grow forever.
                if (stream.Count > 1 << 20)
                {
                    stream.RemoveRange(0, stream.Count - RtpHeaderLength);
                }

                return finished;
            }

            var next = FindPacketStart(span, start + RtpHeaderLength);
            if (next < 0)
            {
                // The last packet in the buffer is only complete once the next one has begun, so
                // wait rather than emitting a half-read fragment.
                if (start > 0)
                {
                    stream.RemoveRange(0, start);
                }

                return finished;
            }

            var packet = span[start..next].ToArray();
            stream.RemoveRange(0, next);
            Consume(packet, finished);
        }
    }

    /// <summary>Whether a packet begins at <paramref name="offset"/>, which has a whole header after it.</summary>
    private bool IsPacketStart(ReadOnlySpan<byte> span, int offset)
    {
        // Version 2, no padding or extension, and the JPEG payload type. The marker bit varies.
        if (span[offset] != 0x80 || (span[offset + 1] & 0x7F) != JpegPayloadType)
        {
            return false;
        }

        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 8)..]);
        return synchronisationSource is null || ssrc == synchronisationSource;
    }

    private int FindPacketStart(ReadOnlySpan<byte> span, int from)
    {
        for (var offset = from; offset + RtpHeaderLength <= span.Length; offset++)
        {
            if (IsPacketStart(span, offset))
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>
    /// Takes one packet, adding any pictures it finished to <paramref name="finished"/>.
    /// </summary>
    /// <remarks>
    /// A single packet can finish two pictures: its fragment offset of zero ends the one before it,
    /// and its marker bit ends its own. That happens whenever a picture fits in one packet.
    /// </remarks>
    private void Consume(byte[] packet, List<CameraFrame> finished)
    {
        if (packet.Length < RtpHeaderLength + JpegHeaderLength)
        {
            return;
        }

        // The first packet fixes the source. From then on a packet from any other sender is never
        // split out at all — IsPacketStart does not recognise its header — so its bytes are skipped
        // as noise rather than mixed into this picture.
        synchronisationSource ??= BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));

        var marker = (packet[1] & 0x80) != 0;
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        if (lastSequence is { } previous && (ushort)(previous + 1) != sequence)
        {
            PacketsLost++;
        }

        lastSequence = sequence;
        PacketsRead++;

        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));

        // The JPEG header follows the RTP header directly: IsPacketStart accepts only a first byte
        // of 0x80, so no packet that gets here lists contributing sources in between. RFC 2435:
        // type-specific, a 24-bit fragment offset, type, Q, then width and height in units of
        // eight pixels.
        const int jpegHeader = RtpHeaderLength;
        var fragmentOffset = (packet[jpegHeader + 1] << 16)
            | (packet[jpegHeader + 2] << 8)
            | packet[jpegHeader + 3];
        var quantisation = packet[jpegHeader + 5];
        var payload = jpegHeader + JpegHeaderLength;

        if (quantisation >= 128 && fragmentOffset == 0 && packet.Length >= payload + 4)
        {
            // Tables are inline. This camera does not use them — it sends a whole JFIF header
            // instead — but skipping them correctly costs one line and keeps this honest RFC 2435.
            var tableLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(payload + 2));
            payload += 4 + tableLength;
        }

        if (payload > packet.Length)
        {
            return;
        }

        if (fragmentOffset == 0)
        {
            // A new picture starts. Anything still being built belongs to the one before it, which
            // is how a camera that never sets the marker bit still produces frames.
            if (building && frame.Count > 0)
            {
                finished.Add(Finish());
            }

            frame.Clear();
            building = true;
            currentTimestamp = timestamp;
            width = packet[jpegHeader + 6] * 8;
            height = packet[jpegHeader + 7] * 8;
        }

        if (!building)
        {
            // Joined the stream mid-picture. Those fragments can never make a whole file, so they
            // are dropped rather than written out as a broken one.
            return;
        }

        frame.AddRange(packet.AsSpan(payload));

        if (marker)
        {
            // RFC 2435 marks the last packet of a picture, which is what lets a frame be delivered
            // as soon as it is whole rather than when the next one begins.
            finished.Add(Finish());
            frame.Clear();
            building = false;
        }
    }

    private CameraFrame Finish()
    {
        var jpeg = frame.ToArray();
        // The camera ends its frames properly, but a dropped last fragment would otherwise produce
        // a file no decoder will open.
        if (jpeg.Length < 2 || jpeg[^2] != 0xFF || jpeg[^1] != 0xD9)
        {
            jpeg = [.. jpeg, 0xFF, 0xD9];
        }

        return new CameraFrame(jpeg, currentTimestamp, width, height);
    }

    /// <summary>Whether a block of bytes looks like a complete JPEG.</summary>
    public static bool LooksLikeJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 4
        && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
        && bytes[^2] == 0xFF && bytes[^1] == 0xD9;
}
