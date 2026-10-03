using System.Net;
using Shouldly;
using Sightline.Core.Connectivity;
using ConnectivityAdapter = Sightline.Core.Connectivity.WifiAdapter;
using Xunit;
using static Sightline.Core.Tests.Connectivity.Adapters;

namespace Sightline.Core.Tests.Connectivity;

/// <summary>Joining the camera's Wi-Fi and leaving it, against fakes that record every call.</summary>
public sealed class CameraLinkTests
{
    private const string Camera = "ActionCam_f8160c220c72";
    private const string Ours = "Sightline ActionCam_f8160c220c72";
    private static readonly IPAddress CameraAddress = IPAddress.Parse("192.168.100.1");
    private static readonly IPAddress Leased = IPAddress.Parse("192.168.100.3");
    private static readonly WifiConnection Hotspot = new("Tochi Galaxy", "Tochi Galaxy 2");
    private static readonly CameraLinkTiming Quick = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5));

    private readonly FakeWlan wlan = new();
    private readonly FakeNetwork network = new();

    private CameraLink Link() => new(wlan, network, Quick);

    private static AdapterChoice Choose(ConnectivityAdapter adapter, string? elsewhere = null) =>
        AdapterAdvisor.Assess([adapter], Camera, _ => elsewhere).Single();

    [Fact]
    public async Task A_free_adapter_joins_with_sightlines_own_profile_and_returns_its_address()
    {
        network.Addresses[DongleId] = [Leased];
        var link = Link();

        var local = await link.JoinAsync(Choose(Dongle()), Camera, "12345678", consented: false, CameraAddress, CancellationToken.None);

        local.ShouldBe(Leased);
        link.Adapter!.Id.ShouldBe(DongleId);
        wlan.SavedProfiles[DongleId].ShouldContain($"<name>{Ours}</name>");
        wlan.Calls.ShouldBe([$"save {DongleId}", $"connect {DongleId} {Ours} {Camera}"]);
    }

    [Fact]
    public async Task Leaving_disconnects_and_deletes_only_sightlines_profile()
    {
        network.Addresses[DongleId] = [Leased];
        var link = Link();
        await link.JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None);
        wlan.Calls.Clear();

        var restored = await link.LeaveAsync();

        restored.ShouldBeNull();
        wlan.Calls.ShouldBe([$"disconnect {DongleId}", $"delete {DongleId} {Ours}"]);
        link.Adapter.ShouldBeNull();
    }

    [Fact]
    public async Task An_adapter_taken_off_a_network_is_put_back_on_it_by_its_own_profile()
    {
        network.Addresses[BuiltInId] = [Leased];
        var link = Link();
        await link.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", consented: true, CameraAddress, CancellationToken.None);
        wlan.Calls.Clear();

        var restored = await link.LeaveAsync();

        restored.ShouldBe("Tochi Galaxy");
        wlan.Calls.ShouldBe([$"disconnect {BuiltInId}", $"delete {BuiltInId} {Ours}", $"connect {BuiltInId} Tochi Galaxy 2 Tochi Galaxy"]);
    }

    [Fact]
    public async Task An_adapter_on_another_network_is_not_touched_without_consent()
    {
        var link = Link();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            link.JoinAsync(Choose(BuiltIn(Hotspot)), Camera, "12345678", consented: false, CameraAddress, CancellationToken.None));

        wlan.Calls.ShouldBeEmpty();
        link.Adapter.ShouldBeNull();
    }

    [Fact]
    public async Task An_adapter_already_on_the_camera_is_used_as_it_is_and_left_as_it_was()
    {
        // Windows joined it, or an earlier session did. Sightline did not connect it, so it does not
        // disconnect it, and it never saved a profile, so it deletes none.
        network.Addresses[DongleId] = [Leased];
        var link = Link();

        var local = await link.JoinAsync(Choose(Dongle(new WifiConnection(Camera, Camera))), Camera, "12345678", false, CameraAddress, CancellationToken.None);
        await link.LeaveAsync();

        local.ShouldBe(Leased);
        wlan.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_join_windows_cannot_finish_says_so_and_puts_everything_back()
    {
        wlan.ConnectResults.Enqueue(new WlanConnectResult(false, "the network is not available"));
        var link = Link();

        var failed = await Should.ThrowAsync<CameraLinkException>(() =>
            link.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", true, CameraAddress, CancellationToken.None));

        failed.Failure.ShouldBe(LinkFailure.NotJoined);
        failed.Message.ShouldContain("WiFi could not join ActionCam_f8160c220c72 (the network is not available)");
        wlan.Calls.ShouldBe([
            $"save {BuiltInId}", $"connect {BuiltInId} {Ours} {Camera}",
            $"disconnect {BuiltInId}", $"delete {BuiltInId} {Ours}", $"connect {BuiltInId} Tochi Galaxy 2 Tochi Galaxy"]);
        link.Adapter.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_join_with_no_reason_still_says_what_to_check()
    {
        wlan.ConnectResults.Enqueue(new WlanConnectResult(false, null));

        var failed = await Should.ThrowAsync<CameraLinkException>(() =>
            Link().JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None));

        failed.Message.ShouldStartWith("WiFi 2 could not join ActionCam_f8160c220c72. Check the password");
    }

    [Fact]
    public async Task Joined_but_given_only_a_self_assigned_address_is_explained()
    {
        // The reference camera, 2026-10-02: a second adapter associated and got 169.254.149.39.
        network.Addresses[DongleId] = [IPAddress.Parse("169.254.149.39")];

        var failed = await Should.ThrowAsync<CameraLinkException>(() =>
            Link().JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None));

        failed.Failure.ShouldBe(LinkFailure.NoAddress);
        failed.Message.ShouldContain("gave it no address (Windows fell back to a 169.254 one)");
        failed.Message.ShouldContain("one device at a time");
        wlan.Calls.ShouldContain($"delete {DongleId} {Ours}");
    }

    [Fact]
    public async Task Joined_and_given_nothing_at_all_is_explained_too()
    {
        var failed = await Should.ThrowAsync<CameraLinkException>(() =>
            Link().JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None));

        failed.Message.ShouldContain("gave it no address.");
    }

    [Fact]
    public async Task An_address_from_some_other_network_is_not_mistaken_for_a_self_assigned_one()
    {
        // A dongle still carrying an old corporate address, say: joined, no camera address, and
        // nothing from 169.254 — so the message must not mention the Windows fallback.
        network.Addresses[DongleId] = [IPAddress.Parse("10.20.30.40")];

        var failed = await Should.ThrowAsync<CameraLinkException>(() =>
            Link().JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None));

        failed.Message.ShouldContain("gave it no address.");
        failed.Message.ShouldNotContain("169.254");
    }

    [Fact]
    public async Task The_consent_guard_holds_even_for_a_choice_built_by_hand()
    {
        // Assess never produces this shape, but AdapterChoice is public and the guard must not
        // depend on who built it.
        var odd = new AdapterChoice(Dongle(), AdapterVerdict.LeavesNetwork, false, "x");

        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            Link().JoinAsync(odd, Camera, "12345678", consented: false, CameraAddress, CancellationToken.None));

        refused.Message.ShouldContain("needs the person's say-so");
        wlan.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_address_is_waited_for_while_the_camera_hands_it_out()
    {
        network.Addresses[DongleId] = [Leased];
        network.AddressArrivesAfter = 3;

        var local = await Link().JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None);

        local.ShouldBe(Leased);
    }

    [Fact]
    public async Task Giving_up_while_waiting_puts_everything_back()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var slow = new CameraLink(wlan, network, new CameraLinkTiming(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(5)));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            slow.JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, cancel.Token));

        wlan.Calls.ShouldContain($"delete {DongleId} {Ours}");
        slow.Adapter.ShouldBeNull();
    }

    [Fact]
    public async Task A_password_that_cannot_be_one_is_refused_before_anything_changes()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Link().JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "short", true, CameraAddress, CancellationToken.None));

        wlan.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_camera_at_a_time()
    {
        network.Addresses[DongleId] = [Leased];
        var link = Link();
        await link.JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            link.JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None));
    }

    [Fact]
    public async Task Leaving_when_not_joined_does_nothing()
    {
        (await Link().LeaveAsync()).ShouldBeNull();
        wlan.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dongle_pulled_out_mid_session_still_gets_the_rest_of_the_clean_up()
    {
        // Each step is tried whatever happened to the one before.
        network.Addresses[BuiltInId] = [Leased];
        var lines = new List<string>();
        var link = Link();
        link.Trace = lines.Add;
        await link.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", true, CameraAddress, CancellationToken.None);
        wlan.Failing.Add("disconnect");
        wlan.Failing.Add("delete");

        var restored = await link.LeaveAsync();

        restored.ShouldBe("Tochi Galaxy");
        lines.ShouldContain(line => line.StartsWith("disconnect WiFi: failed", StringComparison.Ordinal));
        lines.ShouldContain(line => line.StartsWith($"delete profile '{Ours}' from WiFi: failed", StringComparison.Ordinal));
        lines.ShouldContain("put WiFi back on Tochi Galaxy: done");
    }

    [Fact]
    public async Task A_network_that_cannot_be_rejoined_is_reported_not_fatal()
    {
        network.Addresses[BuiltInId] = [Leased];
        var lines = new List<string>();
        var link = Link();
        link.Trace = lines.Add;
        await link.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", true, CameraAddress, CancellationToken.None);
        wlan.ConnectResults.Enqueue(new WlanConnectResult(false, "out of range"));

        (await link.LeaveAsync()).ShouldBeNull();
        lines.ShouldContain("put WiFi back on Tochi Galaxy: out of range");
    }

    [Fact]
    public async Task A_rejoin_windows_refuses_outright_is_reported_not_fatal()
    {
        network.Addresses[BuiltInId] = [Leased];
        var lines = new List<string>();
        var link = Link();
        link.Trace = lines.Add;
        await link.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", true, CameraAddress, CancellationToken.None);
        wlan.Failing.Add("connect");

        (await link.LeaveAsync()).ShouldBeNull();
        lines.ShouldContain(line => line.StartsWith("put WiFi back on Tochi Galaxy: failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leaving_quietly_works_without_anyone_listening()
    {
        network.Addresses[BuiltInId] = [Leased];
        var link = Link();
        await link.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", true, CameraAddress, CancellationToken.None);
        wlan.Failing.Add("disconnect");
        wlan.Failing.Add("connect");

        (await link.LeaveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task The_arguments_are_checked()
    {
        Should.Throw<ArgumentNullException>(() => new CameraLink(null!, network));
        Should.Throw<ArgumentNullException>(() => new CameraLink(wlan, null!));
        new CameraLink(wlan, network).Adapter.ShouldBeNull();
        var link = Link();
        await Should.ThrowAsync<ArgumentNullException>(() => link.JoinAsync(null!, Camera, "", false, CameraAddress, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => link.JoinAsync(Choose(Dongle()), "", "", false, CameraAddress, CancellationToken.None));
        await Should.ThrowAsync<ArgumentNullException>(() => link.JoinAsync(Choose(Dongle()), Camera, "", false, null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_join_made_in_one_process_can_be_left_properly_by_another()
    {
        network.Addresses[BuiltInId] = [Leased];
        var first = Link();
        await first.JoinAsync(Choose(BuiltIn(Hotspot), "Ethernet 4"), Camera, "12345678", true, CameraAddress, CancellationToken.None);
        var state = first.State!;
        wlan.Calls.Clear();

        var second = Link();
        second.Resume(state);
        var restored = await second.LeaveAsync();

        state.ShouldBe(new LinkState(BuiltInId, "WiFi", Hotspot, true, Ours));
        restored.ShouldBe("Tochi Galaxy");
        wlan.Calls.ShouldBe([$"disconnect {BuiltInId}", $"delete {BuiltInId} {Ours}", $"connect {BuiltInId} Tochi Galaxy 2 Tochi Galaxy"]);
        second.State.ShouldBeNull();
    }

    [Fact]
    public async Task A_resumed_state_naming_somebody_elses_profile_never_deletes_it()
    {
        // The state comes from a file on disk; a tampered or corrupt one must not be able to delete
        // a profile Sightline did not create.
        var link = Link();
        link.Resume(new LinkState(DongleId, "WiFi 2", null, true, "Tochi Galaxy 2"));

        await link.LeaveAsync();

        wlan.Calls.ShouldBe([$"disconnect {DongleId}"]);
    }

    [Fact]
    public async Task Resuming_needs_a_state_and_no_join_already_held()
    {
        network.Addresses[DongleId] = [Leased];
        var link = Link();
        Should.Throw<ArgumentNullException>(() => link.Resume(null!));
        await link.JoinAsync(Choose(Dongle()), Camera, "12345678", false, CameraAddress, CancellationToken.None);

        Should.Throw<InvalidOperationException>(() => link.Resume(new LinkState(DongleId, "WiFi 2", null, false, null)));
    }

    [Fact]
    public void The_exceptions_carry_their_messages()
    {
        var cause = new IOException("cause");

        new CameraLinkException().Message.ShouldNotBeNullOrWhiteSpace();
        new CameraLinkException("m").Message.ShouldBe("m");
        new CameraLinkException("m", cause).InnerException.ShouldBe(cause);
        new WlanException().Message.ShouldNotBeNullOrWhiteSpace();
        new WlanException("m", cause).InnerException.ShouldBe(cause);
        CameraLinkTiming.Default.AddressTimeout.ShouldBe(TimeSpan.FromSeconds(15));
    }
}
