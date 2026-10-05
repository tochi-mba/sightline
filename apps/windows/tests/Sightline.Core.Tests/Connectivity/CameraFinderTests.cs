using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Testing;
using Xunit;

namespace Sightline.Core.Tests.Connectivity;

/// <summary>Finding the cameras in range, and the adapter to reach each through.</summary>
public sealed class CameraFinderTests
{
    private const string Camera = "ActionCam_f8160c220c72";
    private const string Other = "ActionCam_000000000001";

    private readonly FakeWlan wlan = new();
    private readonly FakeNetwork network = new();

    private CameraFinder Finder() => new(wlan, network);

    [Fact]
    public async Task Every_adapter_scans_and_only_cameras_are_offered()
    {
        wlan.AdapterList.Add(Adapters.Dongle());
        wlan.Visible[Adapters.DongleId] = [new WifiNetwork("Home", 90), new WifiNetwork(Camera, 70)];

        var found = await Finder().FindAsync(null, CancellationToken.None);

        wlan.Calls.ShouldBe([$"scan {Adapters.DongleId}"]);
        var only = found.ShouldHaveSingleItem();
        only.Ssid.ShouldBe(Camera);
        only.AdapterId.ShouldBe(Adapters.DongleId);
        only.AdapterName.ShouldBe("WiFi 2");
        only.SignalPercent.ShouldBe(70);
        only.RequiresConsent.ShouldBeFalse();
        only.StaysOnline.ShouldBeTrue();
        only.Explanation.ShouldBe("WiFi 2 is free. Nothing else on this PC changes.");
        only.DisplayName.ShouldBe($"{Camera}  ·  WiFi 2  ·  70%");
        only.Target("pass-word", consented: false).ShouldBe(new CameraTarget(Adapters.DongleId, Camera, "pass-word", false));
    }

    [Fact]
    public async Task The_choice_that_changes_least_comes_first_whatever_the_signal()
    {
        // The built-in adapter hears the camera better, but taking it would take this PC off its Wi-Fi.
        wlan.AdapterList.Add(Adapters.BuiltIn(new WifiConnection("Home", "Home")));
        wlan.AdapterList.Add(Adapters.Dongle());
        wlan.Visible[Adapters.BuiltInId] = [new WifiNetwork(Camera, 99)];
        wlan.Visible[Adapters.DongleId] = [new WifiNetwork(Camera, 40)];

        var found = await Finder().FindAsync(null, CancellationToken.None);

        found.Select(o => o.AdapterId).ShouldBe([Adapters.DongleId, Adapters.BuiltInId]);
        found[1].RequiresConsent.ShouldBeTrue();
        found[1].StaysOnline.ShouldBeFalse();
    }

    [Fact]
    public async Task Between_equal_choices_the_remembered_adapter_then_the_stronger_signal_wins()
    {
        wlan.AdapterList.Add(Adapters.BuiltIn());
        wlan.AdapterList.Add(Adapters.Dongle());
        wlan.Visible[Adapters.BuiltInId] = [new WifiNetwork(Camera, 50), new WifiNetwork(Other, 90)];
        wlan.Visible[Adapters.DongleId] = [new WifiNetwork(Camera, 60)];

        var remembered = await Finder().FindAsync(Adapters.BuiltInId, CancellationToken.None);
        remembered.Select(o => (o.AdapterId, o.Ssid)).ShouldBe([
            (Adapters.BuiltInId, Other), (Adapters.BuiltInId, Camera), (Adapters.DongleId, Camera)]);

        var fresh = await Finder().FindAsync(null, CancellationToken.None);
        fresh[0].AdapterId.ShouldBe(Adapters.DongleId);
    }

    [Fact]
    public async Task With_nothing_in_range_nothing_is_offered()
    {
        (await Finder().FindAsync(null, CancellationToken.None)).ShouldBeEmpty();

        wlan.AdapterList.Add(Adapters.Dongle());
        (await Finder().FindAsync(null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public void It_needs_wifi_and_the_network()
    {
        Should.Throw<ArgumentNullException>(() => new CameraFinder(null!, network));
        Should.Throw<ArgumentNullException>(() => new CameraFinder(wlan, null!));
        CameraFinder.SsidPrefix.ShouldBe("ActionCam_");
    }
}
