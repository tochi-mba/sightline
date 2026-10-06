using System.Net;

namespace Sightline.Protocol;

/// <summary>
/// The camera's network as this machine reaches it: a connection to any of the camera's ports, and a
/// socket for its stream's datagrams.
/// </summary>
/// <remarks>
/// One seam for both, so whatever put this machine on the camera's network is also what decides which
/// local address every socket is sent from.
/// </remarks>
public interface ICameraSockets
{
    /// <summary>A connection to <paramref name="port"/> on the camera, not yet opened.</summary>
    ICameraTransport Transport(int port);

    /// <summary>A socket for the camera's datagrams, bound and ready.</summary>
    ICameraDatagrams Datagrams();
}

/// <summary>The camera's network, reached from this machine's address on it.</summary>
/// <param name="camera">The camera's address.</param>
/// <param name="local">This machine's address on the camera's network, which every socket is sent from.</param>
public sealed class BoundCameraSockets(IPAddress camera, IPAddress local) : ICameraSockets
{
    /// <summary>The camera's address.</summary>
    public IPAddress Camera { get; } = camera ?? throw new ArgumentNullException(nameof(camera));

    /// <summary>This machine's address on the camera's network.</summary>
    public IPAddress Local { get; } = local ?? throw new ArgumentNullException(nameof(local));

    /// <inheritdoc />
    public ICameraTransport Transport(int port) => new TcpCameraTransport(new IPEndPoint(Camera, port), Local);

    /// <inheritdoc />
    public ICameraDatagrams Datagrams() => new UdpCameraDatagrams(Camera, Local);
}
