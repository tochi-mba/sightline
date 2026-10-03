using System.Net;
using System.Net.Sockets;
using Shouldly;
using Xunit;

namespace Sightline.Core.Tests;

/// <summary>Which of this PC's addresses can reach the camera.</summary>
public sealed class CameraAddressTests
{
    private static readonly IPAddress Camera = IPAddress.Parse("192.168.100.1");

    [Fact]
    public void The_address_on_the_cameras_network_is_the_one_to_send_from()
    {
        var local = CameraAddress.LocalAddressFor(Camera,
            [IPAddress.Parse("10.107.178.52"), IPAddress.Parse("192.168.100.3"), IPAddress.Parse("10.124.73.1")]);

        local.ShouldBe(IPAddress.Parse("192.168.100.3"));
    }

    [Fact]
    public void A_pc_on_no_camera_network_has_no_address_for_it()
    {
        CameraAddress.LocalAddressFor(Camera, [IPAddress.Parse("10.107.178.52"), IPAddress.Parse("192.168.1.10")]).ShouldBeNull();
        CameraAddress.LocalAddressFor(Camera, []).ShouldBeNull();
    }

    [Fact]
    public void The_cameras_own_address_and_ipv6_addresses_are_never_chosen()
    {
        CameraAddress.LocalAddressFor(Camera, [IPAddress.Parse("192.168.100.1"), IPAddress.IPv6Loopback]).ShouldBeNull();
    }

    [Fact]
    public void A_camera_on_another_subnet_is_matched_on_its_own_subnet()
    {
        CameraAddress.LocalAddressFor(IPAddress.Parse("192.168.1.254"), [IPAddress.Parse("192.168.1.20")])
            .ShouldBe(IPAddress.Parse("192.168.1.20"));
    }

    [Fact]
    public void The_family_default_is_the_address_every_camera_in_it_uses()
    {
        CameraAddress.Default.ShouldBe(Camera);
        Should.Throw<ArgumentNullException>(() => CameraAddress.LocalAddressFor(null!, []));
    }

    [Fact]
    public void This_pcs_own_addresses_are_read_from_interfaces_that_are_up()
    {
        // Read-only, against this machine: nothing here touches an adapter.
        var addresses = CameraAddress.LocalIPv4Addresses().ToList();

        addresses.ShouldAllBe(a => a.AddressFamily == AddressFamily.InterNetwork);
        addresses.ShouldContain(IPAddress.Loopback);
        _ = CameraAddress.LocalAddressFor(Camera);
    }
}
