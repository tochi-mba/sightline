using System.Buffers.Binary;
using System.Text;

namespace Sightline.Protocol.GpSock;

/// <summary>
/// The GPSOCKET wire format, as the camera's firmware defines it.
/// </summary>
/// <remarks>
/// <para>
/// A request is the eight-byte tag, a little-endian type, the command split into its two bytes,
/// and the payload. A request carries no length: the camera knows how long each command's payload
/// is. A response is the same but with a little-endian payload size before the payload.
/// </para>
/// <code>
/// request : "GPSOCKET" | uint16 LE type | mode | cmd | payload
/// response: "GPSOCKET" | uint16 LE type | mode | cmd | uint16 LE size | payload
/// </code>
/// <para>
/// The tag is why this port looks dead to every scanner: the firmware compares those eight bytes
/// first and silently drops anything else, with no banner and no error.
/// </para>
/// </remarks>
public static class GpSockFrame
{
    /// <summary>The eight bytes every frame begins with, in both directions.</summary>
    public static ReadOnlySpan<byte> Tag => "GPSOCKET"u8;

    /// <summary>Bytes before the payload in a request.</summary>
    public const int RequestHeaderLength = 12;

    /// <summary>Bytes before the payload in a response: a request header plus the size.</summary>
    public const int ResponseHeaderLength = 14;

    /// <summary>Builds a request frame.</summary>
    /// <param name="command">The command to send.</param>
    /// <param name="payload">Its argument, which is empty for most commands.</param>
    public static byte[] Encode(GpSockCommand command, ReadOnlySpan<byte> payload = default)
    {
        var frame = new byte[RequestHeaderLength + payload.Length];
        Tag.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)GpSockType.Command);
        // The command's two bytes travel separately: the firmware reads (frame[10] << 8) | frame[11].
        frame[10] = (byte)((ushort)command >> 8);
        frame[11] = (byte)((ushort)command & 0xFF);
        payload.CopyTo(frame.AsSpan(RequestHeaderLength));
        return frame;
    }

    /// <summary>
    /// Reads one response from the front of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Bytes received so far, which may hold part of a frame, one, or several.</param>
    /// <param name="response">The frame read, when this returns <see langword="true"/>.</param>
    /// <param name="consumed">How many bytes of <paramref name="buffer"/> the frame used.</param>
    /// <returns>
    /// <see langword="false"/> when the buffer does not yet hold a whole frame, which is not an
    /// error: the caller reads more and asks again.
    /// </returns>
    /// <exception cref="GpSockProtocolException">The bytes are not a GPSOCKET frame at all.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out GpSockResponse response, out int consumed)
    {
        response = default;
        consumed = 0;
        if (buffer.Length < ResponseHeaderLength)
        {
            return false;
        }

        if (!buffer[..8].SequenceEqual(Tag))
        {
            // Resynchronising would hide a real desynchronisation, and there is no framing to
            // resynchronise to. Saying so immediately is what makes that bug findable.
            throw new GpSockProtocolException(
                $"Expected a GPSOCKET frame but the bytes began {Describe(buffer[..8])}.");
        }

        var type = (GpSockType)BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..]);
        var command = (GpSockCommand)((buffer[10] << 8) | buffer[11]);
        int size = BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]);
        if (buffer.Length < ResponseHeaderLength + size)
        {
            return false;
        }

        var payload = buffer.Slice(ResponseHeaderLength, size).ToArray();
        response = new GpSockResponse(type, command, payload);
        consumed = ResponseHeaderLength + size;
        return true;
    }

    private static string Describe(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder("0x");
        foreach (var b in bytes)
        {
            text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}

/// <summary>One answer from the camera.</summary>
/// <param name="Type">Whether the camera accepted the command.</param>
/// <param name="Command">The command being answered.</param>
/// <param name="Payload">The answer, or the refusal code when this is a refusal.</param>
public readonly record struct GpSockResponse(GpSockType Type, GpSockCommand Command, byte[] Payload)
{
    /// <summary>Whether the camera did what was asked.</summary>
    public bool IsAck => Type == GpSockType.Ack;

    /// <summary>Why the camera refused, or <see cref="NakCode.Ok"/> when it did not refuse.</summary>
    public NakCode Nak =>
        Type == GpSockType.Nak && Payload.Length >= 2
            ? (NakCode)BinaryPrimitives.ReadInt16LittleEndian(Payload)
            : NakCode.Ok;

    /// <summary>
    /// Whether this is the empty frame that ends a chunked answer.
    /// </summary>
    /// <remarks>
    /// Long answers — the menu XML, a thumbnail, a file's bytes — arrive as a run of acks with at
    /// most about 242 bytes each, finished by one with nothing in it.
    /// </remarks>
    public bool IsEndOfChunks => IsAck && Payload.Length == 0;
}

/// <summary>The camera sent something that is not a GPSOCKET frame.</summary>
public sealed class GpSockProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GpSockProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public GpSockProtocolException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Creates the exception.</summary>
    public GpSockProtocolException()
    {
    }
}
