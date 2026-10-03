using System.Net;
using Sightline.Core.Connectivity;
using ConnectivityWifiAdapter = Sightline.Core.Connectivity.WifiAdapter;

namespace Sightline.Core.Tests.Connectivity;

/// <summary>Wi-Fi that needs no adapter: it records what it was asked and answers as scripted.</summary>
internal sealed class FakeWlan : IWlanClient
{
    public List<ConnectivityWifiAdapter> AdapterList { get; } = [];

    public Dictionary<Guid, List<WifiNetwork>> Visible { get; } = [];

    public List<string> Calls { get; } = [];

    public Dictionary<Guid, string> SavedProfiles { get; } = [];

    /// <summary>What the next join attempts return, in order; joins after these succeed.</summary>
    public Queue<WlanConnectResult> ConnectResults { get; } = new();

    /// <summary>Calls whose name starts with one of these throw, as Windows does when an adapter is unplugged.</summary>
    public HashSet<string> Failing { get; } = [];

    public IReadOnlyList<ConnectivityWifiAdapter> Adapters() => AdapterList;

    public IReadOnlyList<WifiNetwork> Networks(Guid adapter) => Visible.GetValueOrDefault(adapter) ?? [];

    public Task ScanAsync(Guid adapter, CancellationToken cancellationToken)
    {
        Record($"scan {adapter}");
        return Task.CompletedTask;
    }

    public void SaveProfile(Guid adapter, string profileXml)
    {
        Record($"save {adapter}");
        SavedProfiles[adapter] = profileXml;
    }

    public Task<WlanConnectResult> ConnectAsync(Guid adapter, string profile, string ssid, CancellationToken cancellationToken)
    {
        Record($"connect {adapter} {profile} {ssid}");
        return Task.FromResult(ConnectResults.Count > 0 ? ConnectResults.Dequeue() : new WlanConnectResult(true, null));
    }

    public void Disconnect(Guid adapter) => Record($"disconnect {adapter}");

    public void DeleteProfile(Guid adapter, string profile) => Record($"delete {adapter} {profile}");

    private void Record(string call)
    {
        Calls.Add(call);
        if (Failing.Any(prefix => call.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new WlanException($"failed: {call}");
        }
    }
}

/// <summary>Addresses that appear when the test says, as DHCP's do.</summary>
internal sealed class FakeNetwork : INetworkState
{
    public Dictionary<Guid, List<IPAddress>> Addresses { get; } = [];

    public Dictionary<Guid, string> InternetElsewhere { get; } = [];

    /// <summary>How many times the addresses must be asked for before they appear.</summary>
    public int AddressArrivesAfter { get; set; }

    private int asked;

    public IReadOnlyList<IPAddress> AddressesOn(Guid adapter) =>
        ++asked > AddressArrivesAfter ? Addresses.GetValueOrDefault(adapter) ?? [] : [];

    public string? InternetPathOtherThan(Guid adapter) => InternetElsewhere.GetValueOrDefault(adapter);
}

internal static class Adapters
{
    public static readonly Guid DongleId = Guid.Parse("0d39fa57-97fd-49d4-85e8-d51333000001");
    public static readonly Guid BuiltInId = Guid.Parse("98d9443b-14e2-43cc-ba63-f0bad1000002");

    public static ConnectivityWifiAdapter Dongle(WifiConnection? on = null) =>
        new(DongleId, "WiFi 2", "TP-Link Wireless MU-MIMO USB Adapter", IsExternal: true, on);

    public static ConnectivityWifiAdapter BuiltIn(WifiConnection? on = null) =>
        new(BuiltInId, "WiFi", "Intel(R) Wi-Fi 6 AX201 160MHz", IsExternal: false, on);
}
