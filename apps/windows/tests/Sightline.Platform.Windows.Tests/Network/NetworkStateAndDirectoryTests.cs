using System.Net;
using System.Net.NetworkInformation;
using Shouldly;
using Sightline.Platform.Windows.Network;
using Sightline.Platform.Windows.Wlan;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Network;

/// <summary>Addresses, internet paths and adapter names, from snapshots and from this machine.</summary>
public sealed class NetworkStateAndDirectoryTests
{
    private static readonly IPAddress Camera = IPAddress.Parse("192.168.100.1");
    private static readonly Guid Dongle = Guid.Parse("0d39fa57-97fd-49d4-85e8-d51333000001");
    private static readonly Guid BuiltIn = Guid.Parse("98d9443b-14e2-43cc-ba63-f0bad1000002");
    private static readonly Guid Tether = Guid.Parse("5d46dc7c-be15-11f1-b7ef-d03c1f000003");
    private static readonly Guid HyperV = Guid.Parse("8b130ee5-b124-11f1-b7e5-d5028d000004");

    private static InterfaceSnapshot Nic(Guid id, string name, bool up, string? address, string? gateway) => new(
        id, name, up,
        address is null ? [] : [IPAddress.Parse(address)],
        gateway is null ? [] : [IPAddress.Parse(gateway)]);

    [Fact]
    public void Only_an_adapter_that_is_up_has_addresses()
    {
        // Windows keeps a disconnected adapter's old 192.168.100.2 on record; that is not a link.
        var state = new SystemNetworkState(Camera, () => [
            Nic(Dongle, "WiFi 2", false, "192.168.100.2", "192.168.100.1"),
            Nic(BuiltIn, "WiFi", true, "10.107.178.52", "10.107.178.26"),
        ]);

        state.AddressesOn(Dongle).ShouldBeEmpty();
        state.AddressesOn(BuiltIn).ShouldBe([IPAddress.Parse("10.107.178.52")]);
        state.AddressesOn(Guid.NewGuid()).ShouldBeEmpty();
    }

    [Fact]
    public void Another_up_interface_with_a_real_gateway_is_an_internet_path()
    {
        var state = new SystemNetworkState(Camera, () => [
            Nic(Dongle, "WiFi 2", true, "192.168.100.3", "192.168.100.1"),
            Nic(HyperV, "vEthernet (Default Switch)", true, "172.20.0.1", null),
            Nic(BuiltIn, "WiFi", false, "10.75.146.52", "10.75.146.125"),
            Nic(Tether, "Ethernet 4", true, "10.124.73.1", "10.124.73.85"),
        ]);

        state.InternetPathOtherThan(BuiltIn).ShouldBe("Ethernet 4");
        state.InternetPathOtherThan(Tether).ShouldBeNull();
    }

    [Fact]
    public void A_gateway_on_the_cameras_own_network_is_not_internet()
    {
        // The camera hands out itself as a gateway; being on two cameras is still being offline.
        var state = new SystemNetworkState(Camera, () => [
            Nic(Dongle, "WiFi 2", true, "192.168.100.3", "192.168.100.1"),
            Nic(BuiltIn, "WiFi", true, "192.168.100.4", "192.168.100.254"),
        ]);

        state.InternetPathOtherThan(Tether).ShouldBeNull();
    }

    [Fact]
    public void This_machines_interfaces_are_read_without_changing_anything()
    {
        var snapshot = SystemNetworkState.Take(NetworkInterface.GetAllNetworkInterfaces());
        var state = new SystemNetworkState(Camera);

        snapshot.ShouldNotBeEmpty();
        _ = state.InternetPathOtherThan(Guid.NewGuid());
        state.AddressesOn(Guid.NewGuid()).ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => new SystemNetworkState(null!));
    }

    [Fact]
    public void Adapter_names_come_from_the_network_stack()
    {
        var directory = new AdapterDirectory(() => [(Dongle, "WiFi 2")], _ => null);

        directory.NameOf(Dongle).ShouldBe("WiFi 2");
        directory.NameOf(BuiltIn).ShouldBeNull();
    }

    [Theory]
    [InlineData(@"USB\VID_2357&PID_0138\00E04C000001", true)]
    [InlineData(@"usb\vid_2357", true)]
    [InlineData(@"PCI\VEN_8086&DEV_06F0", false)]
    [InlineData(null, false)]
    public void A_usb_device_is_a_plug_in_adapter(string? plugAndPlayId, bool external)
    {
        new AdapterDirectory(() => [], _ => plugAndPlayId).IsExternal(Dongle).ShouldBe(external);
    }

    [Fact]
    public void A_snapshot_keeps_only_settled_ipv4_addresses_and_real_gateways()
    {
        // 0.0.0.0 is what Windows reports for a profile with no gateway; an address still in
        // duplicate detection is not usable yet; IPv6 is not how this camera family is reached.
        var snapshot = SystemNetworkState.Snapshot(Dongle, "WiFi 2", isUp: true,
            [
                (IPAddress.Parse("192.168.100.3"), true),
                (IPAddress.Parse("192.168.100.9"), false),
                (IPAddress.IPv6Loopback, true),
            ],
            [IPAddress.Any, IPAddress.Parse("192.168.100.1"), IPAddress.IPv6None]);

        snapshot.Addresses.ShouldBe([IPAddress.Parse("192.168.100.3")]);
        snapshot.Gateways.ShouldBe([IPAddress.Parse("192.168.100.1")]);
    }

    [Fact]
    public void An_interface_windows_does_not_name_by_guid_is_left_out()
    {
        NoGuid odd = new();

        SystemNetworkState.Take([odd]).ShouldBeEmpty();
        AdapterDirectory.InterfacesFrom([odd]).ShouldBeEmpty();
    }

    /// <summary>An interface with an id that is not a GUID; nothing past its id is ever read.</summary>
    private sealed class NoGuid : NetworkInterface
    {
        public override string Id => "loopback-pseudo-interface";

        public override string Name => "odd";
    }

    [Fact]
    public void The_real_directory_reads_this_machine_without_changing_anything()
    {
        var any = NetworkInterface.GetAllNetworkInterfaces().First(n => Guid.TryParse(n.Id, out _));
        var directory = new AdapterDirectory();

        directory.NameOf(Guid.Parse(any.Id)).ShouldBe(any.Name);
        directory.IsExternal(Guid.NewGuid()).ShouldBeFalse();
        _ = directory.IsExternal(Guid.Parse(any.Id));
    }
}
