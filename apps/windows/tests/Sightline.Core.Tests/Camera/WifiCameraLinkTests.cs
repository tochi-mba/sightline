using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Testing;
using Sightline.Protocol;
using System.Net;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>Getting the controller onto the camera's network through the adapter the person chose.</summary>
public sealed class WifiCameraLinkTests
{
    private static readonly IPAddress Leased = IPAddress.Parse("192.168.100.3");
    private static readonly CameraLinkTiming QuickJoin = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5));
    private static readonly TimeSpan QuickCheck = TimeSpan.FromMilliseconds(10);
    private static readonly CameraTarget Dongle = new(Adapters.DongleId, "ActionCam_f8160c220c72", "12345678", Consented: false);

    private readonly FakeWlan wlan = new();
    private readonly FakeNetwork network = new();

    private WifiCameraLink Link(CameraTarget? target) => new(wlan, network, () => target, QuickJoin, QuickCheck);

    [Fact]
    public async Task The_chosen_adapter_joins_and_every_socket_is_sent_from_its_address()
    {
        wlan.AdapterList.Add(Adapters.Dongle());
        network.Addresses[Adapters.DongleId] = [Leased];

        var lease = await Link(Dongle).JoinAsync(reconnecting: false, CancellationToken.None);

        var transport = lease.Transport(8081).ShouldBeOfType<TcpCameraTransport>();
        transport.Endpoint.ShouldBe(new IPEndPoint(CameraAddress.Default, 8081));
        transport.BoundTo.ShouldBe(Leased);
        wlan.Calls.ShouldContain(c => c.StartsWith($"connect {Adapters.DongleId}", StringComparison.Ordinal));

        await lease.DisposeAsync();
        lease.Lost.IsCompleted.ShouldBeFalse();
        wlan.Calls.ShouldContain($"disconnect {Adapters.DongleId}");
    }

    [Fact]
    public async Task The_network_is_lost_when_the_adapter_loses_its_address_there()
    {
        wlan.AdapterList.Add(Adapters.Dongle());
        network.Addresses[Adapters.DongleId] = [Leased];
        await using var lease = await Link(Dongle).JoinAsync(reconnecting: true, CancellationToken.None);
        lease.Lost.IsCompleted.ShouldBeFalse();

        network.Addresses[Adapters.DongleId] = [];

        await lease.Lost.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Nothing_is_joined_without_a_camera_chosen_or_with_its_adapter_gone()
    {
        (await Should.ThrowAsync<CameraLinkException>(() => Link(null).JoinAsync(false, CancellationToken.None)))
            .Message.ShouldBe("Choose a camera, and the Wi-Fi adapter to reach it through, first.");

        (await Should.ThrowAsync<CameraLinkException>(() => Link(Dongle).JoinAsync(false, CancellationToken.None)))
            .Message.ShouldContain("no longer there");
        wlan.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_adapter_on_another_network_is_used_only_with_consent()
    {
        wlan.AdapterList.Add(Adapters.BuiltIn(new WifiConnection("Home", "Home")));
        network.Addresses[Adapters.BuiltInId] = [Leased];
        var builtIn = Dongle with { AdapterId = Adapters.BuiltInId };

        await Should.ThrowAsync<InvalidOperationException>(() => Link(builtIn).JoinAsync(false, CancellationToken.None));
        wlan.Calls.ShouldBeEmpty();

        await using var lease = await Link(builtIn with { Consented = true }).JoinAsync(false, CancellationToken.None);
        lease.ShouldNotBeNull();
    }

    [Fact]
    public void Its_parts_are_required()
    {
        Should.Throw<ArgumentNullException>(() => new WifiCameraLink(null!, network, () => null));
        Should.Throw<ArgumentNullException>(() => new WifiCameraLink(wlan, null!, () => null));
        Should.Throw<ArgumentNullException>(() => new WifiCameraLink(wlan, network, null!));
        new WifiCameraLink(wlan, network, () => null).ShouldNotBeNull();
    }
}
