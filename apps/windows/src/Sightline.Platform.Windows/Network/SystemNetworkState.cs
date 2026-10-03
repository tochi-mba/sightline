using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sightline.Core.Connectivity;

namespace Sightline.Platform.Windows.Network;

/// <summary>One network interface, as far as joining a camera is concerned.</summary>
/// <param name="Id">The interface GUID.</param>
/// <param name="Name">What Windows calls it.</param>
/// <param name="IsUp">Whether it is connected.</param>
/// <param name="Addresses">Its settled IPv4 addresses.</param>
/// <param name="Gateways">Its IPv4 default gateways.</param>
internal sealed record InterfaceSnapshot(Guid Id, string Name, bool IsUp, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPAddress> Gateways);

/// <summary>
/// Addresses and internet paths, read from Windows' network stack.
/// </summary>
/// <remarks>
/// <para>
/// An "internet path" is any other interface that is up and has a default gateway outside the
/// camera's own network — the camera advertises itself as a gateway, and a PC on two cameras is
/// still offline. Virtual switches with no gateway (Hyper-V, WSL) do not count, and neither does a
/// VPN on its own: it rides on one of the paths that does.
/// </para>
/// </remarks>
public sealed class SystemNetworkState : INetworkState
{
    private readonly IPAddress camera;
    private readonly Func<IReadOnlyList<InterfaceSnapshot>> snapshot;

    /// <summary>The state of this PC's own interfaces, for a camera at <paramref name="camera"/>.</summary>
    public SystemNetworkState(IPAddress camera)
        : this(camera, () => Take(NetworkInterface.GetAllNetworkInterfaces()))
    {
    }

    internal SystemNetworkState(IPAddress camera, Func<IReadOnlyList<InterfaceSnapshot>> snapshot)
    {
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.snapshot = snapshot;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> AddressesOn(Guid adapter) =>
        snapshot().Where(i => i.Id == adapter && i.IsUp).SelectMany(i => i.Addresses).ToList();

    /// <inheritdoc />
    public string? InternetPathOtherThan(Guid adapter) =>
        snapshot()
            .Where(i => i.Id != adapter && i.IsUp && i.Gateways.Any(gateway => !OnCameraNetwork(gateway)))
            .Select(i => i.Name)
            .FirstOrDefault();

    private bool OnCameraNetwork(IPAddress address) =>
        Core.CameraAddress.LocalAddressFor(camera, [address]) is not null || address.Equals(camera);

    /// <summary>Reads every interface from the network stack.</summary>
    internal static IReadOnlyList<InterfaceSnapshot> Take(IEnumerable<NetworkInterface> all) =>
        all
            .Where(n => Guid.TryParse(n.Id, out _))
            .Select(n => Snapshot(n, n.GetIPProperties()))
            .ToList();

    private static InterfaceSnapshot Snapshot(NetworkInterface network, IPInterfaceProperties properties) => Snapshot(
        Guid.Parse(network.Id),
        network.Name,
        network.OperationalStatus == OperationalStatus.Up,
        properties.UnicastAddresses.Select(u => (u.Address, Settled: u.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)),
        properties.GatewayAddresses.Select(g => g.Address));

    /// <summary>
    /// What counts, from whatever the stack reported.
    /// </summary>
    /// <remarks>
    /// Only settled IPv4 addresses: one still being checked for duplicates is not usable yet. And
    /// only real IPv4 gateways: Windows reports <c>0.0.0.0</c> for some profiles that have none,
    /// which must not read as a way to the internet.
    /// </remarks>
    internal static InterfaceSnapshot Snapshot(
        Guid id, string name, bool isUp,
        IEnumerable<(IPAddress Address, bool Settled)> addresses, IEnumerable<IPAddress> gateways) => new(
        id,
        name,
        isUp,
        addresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.Settled)
            .Select(a => a.Address)
            .ToList(),
        gateways
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            .ToList());
}
