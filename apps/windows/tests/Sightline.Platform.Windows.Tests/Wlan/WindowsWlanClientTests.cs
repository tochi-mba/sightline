using Shouldly;
using Sightline.Core.Connectivity;
using Sightline.Platform.Windows.Wlan;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Wlan;

/// <summary>The Wi-Fi client Sightline uses, against fakes for Windows on both sides.</summary>
public sealed class WindowsWlanClientTests
{
    private static readonly Guid Dongle = Guid.Parse("0d39fa57-97fd-49d4-85e8-d51333000001");
    private static readonly Guid BuiltIn = Guid.Parse("98d9443b-14e2-43cc-ba63-f0bad1000002");
    private static readonly WlanClientTiming Quick = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5));

    private readonly FakeReader reader = new();
    private readonly NetshWlanWriterTests.RecordingRunner runner = new();
    private readonly FakeDirectory directory = new();

    private WindowsWlanClient Client() => new(reader, new NetshWlanWriter(runner), directory, Quick);

    [Fact]
    public void Adapters_carry_windows_names_where_they_plug_in_and_the_network_they_are_on()
    {
        reader.Rows.Add(new InterfaceRow(BuiltIn, "Intel(R) Wi-Fi 6 AX201 160MHz", 1));
        reader.Rows.Add(new InterfaceRow(Dongle, "TP-Link Wireless MU-MIMO USB Adapter", 4));
        reader.Connections[BuiltIn] = new ConnectionRow(1, "Tochi Galaxy 2", "Tochi Galaxy", 90);
        directory.Names[BuiltIn] = "WiFi";
        directory.Names[Dongle] = "WiFi 2";
        directory.External.Add(Dongle);

        var adapters = Client().Adapters();

        adapters.ShouldBe([
            new WifiAdapter(BuiltIn, "WiFi", "Intel(R) Wi-Fi 6 AX201 160MHz", false, new WifiConnection("Tochi Galaxy", "Tochi Galaxy 2")),
            new WifiAdapter(Dongle, "WiFi 2", "TP-Link Wireless MU-MIMO USB Adapter", true, null),
        ]);
    }

    [Fact]
    public void An_adapter_windows_has_no_name_for_is_called_by_its_hardware()
    {
        reader.Rows.Add(new InterfaceRow(Dongle, "TP-Link", 4));

        Client().Adapters().Single().Name.ShouldBe("TP-Link");
    }

    [Fact]
    public void A_connected_adapter_whose_attributes_vanish_mid_read_is_on_no_network()
    {
        reader.Rows.Add(new InterfaceRow(Dongle, "TP-Link", 1));

        Client().Adapters().Single().Connection.ShouldBeNull();
    }

    [Fact]
    public void Networks_are_listed_once_each_strongest_first_with_hidden_ones_left_out()
    {
        reader.Networks.AddRange([new NetworkRow("Weak", 20), new NetworkRow("ActionCam_x", 70), new NetworkRow("ActionCam_x", 95), new NetworkRow("", 99)]);

        Client().Networks(Dongle).ShouldBe([new WifiNetwork("ActionCam_x", 95), new WifiNetwork("Weak", 20)]);
    }

    [Fact]
    public async Task A_scan_asks_the_adapter_and_waits_for_results()
    {
        await Client().ScanAsync(Dongle, CancellationToken.None);

        reader.Scanned.ShouldBe([Dongle]);
    }

    [Fact]
    public void Only_sightlines_own_profiles_are_ever_saved()
    {
        directory.Names[Dongle] = "WiFi 2";
        var client = Client();

        client.SaveProfile(Dongle, WlanProfile.Xml("ActionCam_x", "12345678"));
        runner.Calls.Count.ShouldBe(1);

        Should.Throw<InvalidOperationException>(() => client.SaveProfile(Dongle, "<WLANProfile><name>Tochi Galaxy 2</name></WLANProfile>"));
        Should.Throw<InvalidOperationException>(() => client.SaveProfile(Dongle, "<WLANProfile/>"));
        Should.Throw<InvalidOperationException>(() => client.SaveProfile(Dongle, "<WLANProfile><name>Sightline x"));
        Should.Throw<InvalidOperationException>(() => client.SaveProfile(Dongle,
            "<WLANProfile><SSIDConfig><SSID><name>Sightline x</name></SSID></SSIDConfig></WLANProfile>"));
        Should.Throw<ArgumentNullException>(() => client.SaveProfile(Dongle, null!));
        runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public void A_profile_name_escaped_in_the_xml_is_read_as_it_really_is()
    {
        directory.Names[Dongle] = "WiFi 2";

        Client().SaveProfile(Dongle, WlanProfile.Xml("Cam & Co", "12345678"));

        runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public void Only_sightlines_own_profiles_are_ever_deleted()
    {
        directory.Names[Dongle] = "WiFi 2";
        var client = Client();

        client.DeleteProfile(Dongle, "Sightline ActionCam_x");
        Should.Throw<InvalidOperationException>(() => client.DeleteProfile(Dongle, "ActionCam_x"));

        runner.Calls.Select(c => c.Arguments).ShouldBe(["wlan delete profile name=\"Sightline ActionCam_x\" interface=\"WiFi 2\""]);
    }

    [Fact]
    public async Task Joining_waits_until_the_adapter_reports_the_network()
    {
        directory.Names[Dongle] = "WiFi 2";
        reader.ConnectionAppearsAfter = 2;
        reader.Connections[Dongle] = new ConnectionRow(1, "Sightline ActionCam_x", "ActionCam_x", 100);

        var result = await Client().ConnectAsync(Dongle, "Sightline ActionCam_x", "ActionCam_x", CancellationToken.None);

        result.ShouldBe(new WlanConnectResult(true, null));
        runner.Calls.Single().Arguments.ShouldBe("wlan connect name=\"Sightline ActionCam_x\" ssid=\"ActionCam_x\" interface=\"WiFi 2\"");
    }

    [Fact]
    public async Task A_join_that_never_lands_says_how_long_was_waited()
    {
        directory.Names[Dongle] = "WiFi 2";
        reader.Connections[Dongle] = new ConnectionRow(1, "Tochi Galaxy 2", "Tochi Galaxy", 100);

        var result = await Client().ConnectAsync(Dongle, "Sightline ActionCam_x", "ActionCam_x", CancellationToken.None);

        result.Joined.ShouldBeFalse();
        result.Reason.ShouldBe("Windows had not joined it after 0 seconds");
    }

    [Fact]
    public void Leaving_takes_the_named_adapter_off_its_network()
    {
        directory.Names[Dongle] = "WiFi 2";

        Client().Disconnect(Dongle);

        runner.Calls.Single().Arguments.ShouldBe("wlan disconnect interface=\"WiFi 2\"");
    }

    [Fact]
    public void An_adapter_that_was_unplugged_is_reported_as_gone()
    {
        var gone = Should.Throw<WlanException>(() => Client().Disconnect(Dongle));

        gone.Message.ShouldContain("unplugged");
    }

    [Fact]
    public void The_real_client_lists_this_pcs_adapters_without_changing_anything()
    {
        new WindowsWlanClient().Adapters().ShouldAllBe(a => a.Name.Length > 0);
        WlanClientTiming.Default.ScanWait.ShouldBe(TimeSpan.FromSeconds(4));
    }

    private sealed class FakeReader : IWlanReader
    {
        private int asked;

        public List<InterfaceRow> Rows { get; } = [];

        public Dictionary<Guid, ConnectionRow> Connections { get; } = [];

        public List<NetworkRow> Networks { get; } = [];

        public List<Guid> Scanned { get; } = [];

        public int ConnectionAppearsAfter { get; set; }

        public IReadOnlyList<InterfaceRow> Interfaces() => Rows;

        public ConnectionRow? Connection(Guid adapter) =>
            ++asked > ConnectionAppearsAfter ? Connections.GetValueOrDefault(adapter) : null;

        IReadOnlyList<NetworkRow> IWlanReader.Networks(Guid adapter) => Networks;

        public void Scan(Guid adapter) => Scanned.Add(adapter);
    }

    private sealed class FakeDirectory : IAdapterDirectory
    {
        public Dictionary<Guid, string> Names { get; } = [];

        public HashSet<Guid> External { get; } = [];

        public string? NameOf(Guid adapter) => Names.GetValueOrDefault(adapter);

        public bool IsExternal(Guid adapter) => External.Contains(adapter);
    }
}
