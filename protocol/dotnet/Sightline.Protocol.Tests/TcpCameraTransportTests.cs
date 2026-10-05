using System.Net;
using System.Net.Sockets;
using Shouldly;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The real socket transport, against a listener on this machine's loopback address.
/// </summary>
/// <remarks>
/// Loopback only: these tests never touch a Wi-Fi adapter or any network a person depends on.
/// </remarks>
public sealed class TcpCameraTransportTests
{
    [Fact]
    public async Task Bytes_sent_arrive_and_bytes_received_are_returned()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        await using var transport = new TcpCameraTransport(endpoint, IPAddress.Loopback);

        var accepting = listener.AcceptSocketAsync();
        await transport.ConnectAsync(CancellationToken.None);
        using var camera = await accepting;

        transport.IsConnected.ShouldBeTrue();
        await transport.SendAsync("GPSOCKET"u8.ToArray(), CancellationToken.None);
        var heard = new byte[8];
        var got = 0;
        while (got < heard.Length)
        {
            got += await camera.ReceiveAsync(heard.AsMemory(got), SocketFlags.None);
        }

        heard.ShouldBe("GPSOCKET"u8.ToArray());

        await camera.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);
        var into = new byte[16];
        var read = await transport.ReceiveAsync(into, CancellationToken.None);
        into.AsSpan(0, read).ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task A_camera_closing_the_connection_reads_as_zero_bytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var transport = new TcpCameraTransport((IPEndPoint)listener.LocalEndpoint);

        var accepting = listener.AcceptSocketAsync();
        await transport.ConnectAsync(CancellationToken.None);
        (await accepting).Dispose();

        (await transport.ReceiveAsync(new byte[4], CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Nothing_can_be_sent_or_read_before_connecting_or_after_closing()
    {
        var transport = new TcpCameraTransport(new IPEndPoint(IPAddress.Loopback, 9));

        transport.IsConnected.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => transport.SendAsync(new byte[1], CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => transport.ReceiveAsync(new byte[1], CancellationToken.None));

        await transport.DisposeAsync();
        transport.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_connection_is_reported_and_leaves_nothing_open()
    {
        // A port that was just released has nobody listening on it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        await using var transport = new TcpCameraTransport(endpoint, IPAddress.Loopback);

        await Should.ThrowAsync<SocketException>(() => transport.ConnectAsync(CancellationToken.None));

        transport.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void A_transport_needs_somewhere_to_connect()
    {
        Should.Throw<ArgumentNullException>(() => new TcpCameraTransport(null!));
        var bound = new TcpCameraTransport(new IPEndPoint(IPAddress.Loopback, 8081), IPAddress.Loopback);
        bound.Endpoint.ShouldBe(new IPEndPoint(IPAddress.Loopback, 8081));
        bound.BoundTo.ShouldBe(IPAddress.Loopback);
        new TcpCameraTransport(new IPEndPoint(IPAddress.Loopback, 8081)).BoundTo.ShouldBeNull();
    }
}
