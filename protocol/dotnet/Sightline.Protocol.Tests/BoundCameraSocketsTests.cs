using System.Net;
using Shouldly;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>The camera's sockets from one local address: every connection and datagram is sent from it.</summary>
public sealed class BoundCameraSocketsTests
{
    [Fact]
    public void Connections_go_to_the_camera_from_the_local_address()
    {
        var sockets = new BoundCameraSockets(IPAddress.Parse("192.168.100.1"), IPAddress.Loopback);

        var transport = sockets.Transport(8081).ShouldBeOfType<TcpCameraTransport>();

        transport.Endpoint.ShouldBe(new IPEndPoint(IPAddress.Parse("192.168.100.1"), 8081));
        transport.BoundTo.ShouldBe(IPAddress.Loopback);
        sockets.Camera.ShouldBe(IPAddress.Parse("192.168.100.1"));
        sockets.Local.ShouldBe(IPAddress.Loopback);
    }

    [Fact]
    public async Task The_streams_socket_is_bound_on_the_local_address()
    {
        var sockets = new BoundCameraSockets(IPAddress.Loopback, IPAddress.Loopback);

        await using var datagrams = sockets.Datagrams();

        datagrams.ShouldBeOfType<UdpCameraDatagrams>().Port.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Sockets_need_both_addresses()
    {
        Should.Throw<ArgumentNullException>(() => new BoundCameraSockets(null!, IPAddress.Loopback));
        Should.Throw<ArgumentNullException>(() => new BoundCameraSockets(IPAddress.Loopback, null!));
    }
}
