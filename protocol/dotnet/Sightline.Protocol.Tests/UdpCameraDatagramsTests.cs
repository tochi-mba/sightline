using System.Net;
using System.Net.Sockets;
using Shouldly;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The real datagram socket, against sockets standing in for the camera on this machine's loopback addresses.
/// </summary>
/// <remarks>
/// Loopback only: these tests never touch a Wi-Fi adapter or any network a person depends on.
/// </remarks>
public sealed class UdpCameraDatagramsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Datagrams_from_the_camera_arrive_and_datagrams_sent_reach_the_port_named()
    {
        using var camera = Socket(IPAddress.Loopback);
        await using var datagrams = new UdpCameraDatagrams(IPAddress.Loopback, IPAddress.Loopback);
        datagrams.Port.ShouldBeGreaterThan(0);

        await datagrams.SendAsync(new byte[] { 0 }, Port(camera), CancellationToken.None);
        var heard = new byte[16];
        var from = await camera.ReceiveFromAsync(heard, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0));
        from.ReceivedBytes.ShouldBe(1);
        ((IPEndPoint)from.RemoteEndPoint).Port.ShouldBe(datagrams.Port);

        await camera.SendToAsync(new byte[] { 0x80, 0x9A, 7 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, datagrams.Port));
        var into = new byte[16];
        using var patience = new CancellationTokenSource(Patience);
        var length = await datagrams.ReceiveAsync(into, patience.Token);
        into[..length].ShouldBe(new byte[] { 0x80, 0x9A, 7 });
    }

    [Fact]
    public async Task Datagrams_from_anybody_but_the_camera_are_not_its_picture()
    {
        // Another phone on the camera's network could send anything to this port.
        using var camera = Socket(IPAddress.Loopback);
        using var stranger = Socket(IPAddress.Parse("127.0.0.2"));
        await using var datagrams = new UdpCameraDatagrams(IPAddress.Loopback, IPAddress.Loopback);
        var to = new IPEndPoint(IPAddress.Loopback, datagrams.Port);

        await stranger.SendToAsync(new byte[] { 0x66 }, SocketFlags.None, to);
        await camera.SendToAsync(new byte[] { 0x80 }, SocketFlags.None, to);
        var into = new byte[16];
        using var patience = new CancellationTokenSource(Patience);
        var length = await datagrams.ReceiveAsync(into, patience.Token);

        into[..length].ShouldBe(new byte[] { 0x80 });
    }

    [Fact]
    public async Task A_port_unreachable_report_for_an_earlier_send_does_not_end_the_stream()
    {
        // Windows hands the ICMP report for a send nobody heard to the next receive, as a failure.
        using var camera = Socket(IPAddress.Loopback);
        await using var datagrams = new UdpCameraDatagrams(IPAddress.Loopback, IPAddress.Loopback);
        int closed;
        using (var gone = Socket(IPAddress.Loopback))
        {
            closed = Port(gone);
        }

        await datagrams.SendAsync(new byte[] { 0 }, closed, CancellationToken.None);
        await Task.Delay(100);
        await camera.SendToAsync(new byte[] { 0x80, 1 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, datagrams.Port));
        var into = new byte[16];
        using var patience = new CancellationTokenSource(Patience);
        var length = await datagrams.ReceiveAsync(into, patience.Token);

        into[..length].ShouldBe(new byte[] { 0x80, 1 });
    }

    [Fact]
    public async Task Waiting_for_a_datagram_can_be_given_up()
    {
        await using var datagrams = new UdpCameraDatagrams(IPAddress.Loopback, IPAddress.Loopback);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => datagrams.ReceiveAsync(new byte[16], cancel.Token));
    }

    [Fact]
    public async Task A_closed_socket_receives_nothing_more()
    {
        var datagrams = new UdpCameraDatagrams(IPAddress.Loopback, IPAddress.Loopback);

        await datagrams.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => datagrams.ReceiveAsync(new byte[16], CancellationToken.None));
    }

    [Fact]
    public void An_address_this_machine_does_not_have_is_refused_and_leaves_nothing_open()
    {
        // 192.0.2.1 is reserved for documentation, so no machine has it.
        var refused = Should.Throw<SocketException>(() => new UdpCameraDatagrams(IPAddress.Loopback, IPAddress.Parse("192.0.2.1")));

        refused.SocketErrorCode.ShouldBe(SocketError.AddressNotAvailable);
    }

    [Fact]
    public void A_socket_needs_both_addresses()
    {
        Should.Throw<ArgumentNullException>(() => new UdpCameraDatagrams(null!, IPAddress.Loopback));
        Should.Throw<ArgumentNullException>(() => new UdpCameraDatagrams(IPAddress.Loopback, null!));
    }

    private static Socket Socket(IPAddress address)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address, 0));
        return socket;
    }

    private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;
}
