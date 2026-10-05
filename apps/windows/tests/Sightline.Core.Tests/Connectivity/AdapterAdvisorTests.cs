using ConnectivityAdapter = Sightline.Core.Connectivity.WifiAdapter;
using Shouldly;
using Sightline.Core.Connectivity;
using Xunit;
using static Sightline.Core.Testing.Adapters;

namespace Sightline.Core.Tests.Connectivity;

/// <summary>Which adapter the camera uses, and what each choice is said to cost.</summary>
public sealed class AdapterAdvisorTests
{
    private const string Camera = "ActionCam_f8160c220c72";
    private static readonly WifiConnection Hotspot = new("Tochi Galaxy", "Tochi Galaxy 2");

    private static IReadOnlyList<AdapterChoice> Assess(params ConnectivityAdapter[] adapters) =>
        AdapterAdvisor.Assess(adapters, Camera, _ => null);

    [Fact]
    public void An_adapter_already_on_the_camera_needs_nothing_changed()
    {
        var choice = Assess(Dongle(new WifiConnection(Camera, "Sightline " + Camera))).Single();

        choice.Verdict.ShouldBe(AdapterVerdict.OnCamera);
        choice.StaysOnline.ShouldBeTrue();
        choice.NeedsConsent.ShouldBeFalse();
        choice.Explanation.ShouldBe("WiFi 2 is already on ActionCam_f8160c220c72.");
    }

    [Fact]
    public void A_free_adapter_changes_nothing_else()
    {
        var choice = Assess(Dongle()).Single();

        choice.Verdict.ShouldBe(AdapterVerdict.Free);
        choice.NeedsConsent.ShouldBeFalse();
        choice.Explanation.ShouldBe("WiFi 2 is free. Nothing else on this PC changes.");
    }

    [Fact]
    public void An_adapter_on_the_only_internet_says_this_pc_goes_offline()
    {
        // The owner's laptop on 2026-10-02: the built-in adapter on the phone's hotspot.
        var choice = Assess(BuiltIn(Hotspot)).Single();

        choice.Verdict.ShouldBe(AdapterVerdict.LeavesNetwork);
        choice.NeedsConsent.ShouldBeTrue();
        choice.StaysOnline.ShouldBeFalse();
        choice.Explanation.ShouldContain("would leave Tochi Galaxy, and this PC would be offline");
        choice.Explanation.ShouldContain("puts it back on Tochi Galaxy");
    }

    [Fact]
    public void An_adapter_on_a_network_says_which_connection_keeps_the_pc_online()
    {
        var choice = AdapterAdvisor.Assess([BuiltIn(Hotspot)], Camera, _ => "Ethernet 4").Single();

        choice.StaysOnline.ShouldBeTrue();
        choice.NeedsConsent.ShouldBeTrue();
        choice.Explanation.ShouldContain("This PC stays online through Ethernet 4");
    }

    [Fact]
    public void An_adapter_on_another_network_is_never_recommended()
    {
        // Taking somebody's adapter off their network is only ever their own choice.
        var choices = AdapterAdvisor.Assess([BuiltIn(Hotspot)], Camera, _ => "Ethernet 4");

        AdapterAdvisor.Recommend(choices, remembered: BuiltInId).ShouldBeNull();
    }

    [Fact]
    public void A_free_plug_in_adapter_is_preferred_to_a_free_built_in_one()
    {
        var choice = AdapterAdvisor.Recommend(Assess(BuiltIn(), Dongle()), remembered: null);

        choice!.Adapter.Id.ShouldBe(DongleId);
    }

    [Fact]
    public void The_adapter_that_reached_the_camera_last_time_is_preferred_among_free_ones()
    {
        // The camera's DHCP remembers it; a different adapter can end up with no address.
        var choice = AdapterAdvisor.Recommend(Assess(Dongle(), BuiltIn()), remembered: BuiltInId);

        choice!.Adapter.Id.ShouldBe(BuiltInId);
    }

    [Fact]
    public void An_adapter_already_on_the_camera_beats_everything()
    {
        var choice = AdapterAdvisor.Recommend(
            Assess(Dongle(), BuiltIn(new WifiConnection(Camera, Camera))), remembered: DongleId);

        choice!.Verdict.ShouldBe(AdapterVerdict.OnCamera);
        choice.Adapter.Id.ShouldBe(BuiltInId);
    }

    [Fact]
    public void Ranking_puts_adapters_that_leave_a_network_last_and_those_that_strand_the_pc_after_those_that_do_not()
    {
        var third = new WifiAdapter(Guid.NewGuid(), "WiFi 3", "Realtek", true, new WifiConnection("Office", "Office"));
        var choices = AdapterAdvisor.Assess(
            [BuiltIn(Hotspot), third, Dongle()], Camera, a => a.Id == third.Id ? "Ethernet" : null);

        var ranked = AdapterAdvisor.Rank(choices, remembered: null);

        ranked.Select(c => c.Adapter.Name).ShouldBe(["WiFi 2", "WiFi 3", "WiFi"]);
    }

    [Fact]
    public void Ranking_breaks_ties_by_name_so_the_order_never_shuffles()
    {
        var a = new WifiAdapter(Guid.NewGuid(), "WiFi B", "x", true, null);
        var b = new WifiAdapter(Guid.NewGuid(), "WiFi A", "x", true, null);

        AdapterAdvisor.Rank(Assess(a, b), null).Select(c => c.Adapter.Name).ShouldBe(["WiFi A", "WiFi B"]);
        Should.Throw<ArgumentNullException>(() => AdapterAdvisor.Rank(null!, null));
    }

    [Fact]
    public void A_pc_with_no_wifi_has_nothing_to_recommend()
    {
        AdapterAdvisor.Recommend(Assess(), remembered: null).ShouldBeNull();
    }

    [Fact]
    public void The_arguments_are_checked()
    {
        Should.Throw<ArgumentNullException>(() => AdapterAdvisor.Assess(null!, Camera, _ => null));
        Should.Throw<ArgumentException>(() => AdapterAdvisor.Assess([], " ", _ => null));
        Should.Throw<ArgumentNullException>(() => AdapterAdvisor.Assess([], Camera, null!));
        Should.Throw<ArgumentNullException>(() => AdapterAdvisor.Recommend(null!, null));
    }

    [Fact]
    public void An_adapter_knows_whether_it_is_idle_and_where_it_is()
    {
        Dongle().IsIdle.ShouldBeTrue();
        BuiltIn(Hotspot).IsIdle.ShouldBeFalse();
        BuiltIn(Hotspot).IsOn("Tochi Galaxy").ShouldBeTrue();
        BuiltIn(Hotspot).IsOn(Camera).ShouldBeFalse();
        Dongle().IsOn(Camera).ShouldBeFalse();
    }

    [Fact]
    public void A_seen_network_is_a_value_so_two_sightings_of_the_same_one_are_equal()
    {
        new WifiNetwork(Camera, 95).ShouldBe(new WifiNetwork(Camera, 95));
        new WifiNetwork(Camera, 95).ShouldNotBe(new WifiNetwork(Camera, 40));
    }
}
