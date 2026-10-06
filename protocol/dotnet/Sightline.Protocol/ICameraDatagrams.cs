using System.Net;
using System.Net.Sockets;

namespace Sightline.Protocol;

/// <summary>
/// The datagrams the camera's stream arrives in.
/// </summary>
/// <remarks>
/// <para>
/// The camera's pictures come over UDP. It will also send them down its RTSP connection, but in that mode
/// it answers one stream each time it is switched on, and once that stream ends its own buttons stop
/// working until its battery comes out; see PROTOCOL.md, "The stream goes over UDP".
/// </para>
/// <para>
/// A seam for the same two reasons as <see cref="ICameraTransport"/>: tests stand a fake camera behind it,
/// and the real one is bound to the adapter the camera is on.
/// </para>
/// </remarks>
public interface ICameraDatagrams : IAsyncDisposable
{
    /// <summary>The local port the datagrams arrive at, which SETUP tells the camera.</summary>
    int Port { get; }

    /// <summary>Sends one datagram to <paramref name="port"/> on the camera.</summary>
    Task SendAsync(ReadOnlyMemory<byte> datagram, int port, CancellationToken cancellationToken);

    /// <summary>Waits for the next datagram from the camera, returning its length.</summary>
    Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken);
}

/// <summary>
/// A UDP socket on the camera's network, bound to this machine's address there, that hears only the camera.
/// </summary>
/// <remarks>
/// The port is left to the system: a fixed one can fall in a range Windows has reserved for itself, which
/// refuses the bind outright, and the camera takes any port it is told, odd or even.
/// </remarks>
public sealed class UdpCameraDatagrams : ICameraDatagrams
{
    private readonly IPAddress camera;
    private readonly Socket socket;

    /// <summary>Opens a socket on <paramref name="local"/> for datagrams from <paramref name="camera"/>.</summary>
    /// <param name="camera">The camera's address: datagrams from anywhere else are not its stream.</param>
    /// <param name="local">This machine's address on the camera's network.</param>
    public UdpCameraDatagrams(IPAddress camera, IPAddress local)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(local);
        this.camera = camera;
        socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(local, 0));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> datagram, int port, CancellationToken cancellationToken) =>
        await socket.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(camera, port), cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                // The endpoint passed in only says which family to expect; the sender comes back in the result.
                received = await socket.ReceiveFromAsync(into, SocketFlags.None, socket.LocalEndPoint!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException reset) when (reset.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an ICMP "port unreachable" for an earlier send as a failed receive. It says
                // nothing about this socket's stream, which carries on.
                continue;
            }

            if (((IPEndPoint)received.RemoteEndPoint).Address.Equals(camera))
            {
                return received.ReceivedBytes;
            }

            // Anybody else on the camera's network: not the camera's picture, whatever it looks like.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
