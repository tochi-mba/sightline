namespace Sightline.Core.Connectivity;

/// <summary>A Wi-Fi adapter on this PC.</summary>
/// <param name="Id">The interface GUID, which survives renames.</param>
/// <param name="Name">What Windows calls it, such as <c>WiFi 2</c>.</param>
/// <param name="Description">The hardware, such as <c>TP-Link Wireless MU-MIMO USB Adapter</c>.</param>
/// <param name="IsExternal">Whether it is plugged in (USB) rather than built into the PC.</param>
/// <param name="Connection">The network it is on, or null when it is on none.</param>
public sealed record WifiAdapter(Guid Id, string Name, string Description, bool IsExternal, WifiConnection? Connection)
{
    /// <summary>Whether it is on no network at all.</summary>
    public bool IsIdle => Connection is null;

    /// <summary>Whether it is on <paramref name="ssid"/>.</summary>
    public bool IsOn(string ssid) => Connection is { } connection && connection.Ssid == ssid;
}

/// <summary>The network an adapter is on.</summary>
/// <param name="Ssid">The network's name.</param>
/// <param name="Profile">
/// The saved profile Windows used to join it. Kept so the adapter can be put back on exactly this
/// network afterwards — the profile name and the network name differ more often than not.
/// </param>
public sealed record WifiConnection(string Ssid, string Profile);

/// <summary>A network an adapter can see.</summary>
/// <param name="Ssid">The network's name.</param>
/// <param name="SignalPercent">Signal quality, 0 to 100, as Windows reports it.</param>
public sealed record WifiNetwork(string Ssid, int SignalPercent);
