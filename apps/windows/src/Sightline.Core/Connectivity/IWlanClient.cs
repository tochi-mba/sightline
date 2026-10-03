using System.Net;

namespace Sightline.Core.Connectivity;

/// <summary>
/// This PC's Wi-Fi, as Sightline needs it.
/// </summary>
/// <remarks>
/// The real implementation is the Windows Native Wi-Fi API, which answers in structures rather than
/// in netsh's text — text that is translated on a German or Japanese Windows and would stop being
/// parseable there. Everything above this interface is tested against a fake; nothing in a test
/// ever touches a real adapter.
/// </remarks>
public interface IWlanClient
{
    /// <summary>Every Wi-Fi adapter, and the network each is on.</summary>
    IReadOnlyList<WifiAdapter> Adapters();

    /// <summary>The networks <paramref name="adapter"/> saw in its last scan.</summary>
    IReadOnlyList<WifiNetwork> Networks(Guid adapter);

    /// <summary>Asks <paramref name="adapter"/> to scan, and waits until it has or a few seconds pass.</summary>
    Task ScanAsync(Guid adapter, CancellationToken cancellationToken);

    /// <summary>Saves a profile on <paramref name="adapter"/>, replacing one of the same name.</summary>
    void SaveProfile(Guid adapter, string profileXml);

    /// <summary>Joins <paramref name="ssid"/> with the saved <paramref name="profile"/>, and says whether it worked.</summary>
    Task<WlanConnectResult> ConnectAsync(Guid adapter, string profile, string ssid, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="adapter"/> off whatever network it is on.</summary>
    void Disconnect(Guid adapter);

    /// <summary>Deletes the saved <paramref name="profile"/> from <paramref name="adapter"/>.</summary>
    void DeleteProfile(Guid adapter, string profile);
}

/// <summary>Windows' Wi-Fi service refused or failed a request.</summary>
public sealed class WlanException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WlanException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public WlanException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public WlanException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>How an attempt to join a network ended.</summary>
/// <param name="Joined">Whether the adapter is now on the network.</param>
/// <param name="Reason">Windows' reason when it is not, in words.</param>
public sealed record WlanConnectResult(bool Joined, string? Reason);

/// <summary>
/// The addresses and routes this PC has, as far as joining a camera is concerned.
/// </summary>
public interface INetworkState
{
    /// <summary>
    /// The settled IPv4 addresses on <paramref name="adapter"/>, or none while it is down.
    /// </summary>
    /// <remarks>
    /// Windows keeps a disconnected adapter's old address on record, so an address alone proves
    /// nothing; only one on an adapter that is up and has finished acquiring it counts.
    /// </remarks>
    IReadOnlyList<IPAddress> AddressesOn(Guid adapter);

    /// <summary>
    /// Another connection that reaches the internet — Ethernet, USB tethering, another Wi-Fi
    /// adapter — named as Windows names it, or null when <paramref name="adapter"/> is the only one.
    /// </summary>
    string? InternetPathOtherThan(Guid adapter);
}
