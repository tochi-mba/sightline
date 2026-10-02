using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sightline.Core;

/// <summary>
/// Where the camera is, and which of this machine's addresses can reach it.
/// </summary>
/// <remarks>
/// <para>
/// Every socket to the camera is bound to the local address on the camera's network. On a machine
/// with only one network that is a formality; on one with Ethernet, a tethered phone or a VPN as
/// well, it is the difference between the traffic reaching the camera and leaving quietly over the
/// wrong interface — which reads exactly like the camera being switched off.
/// </para>
/// <para>
/// The address is found from the interface list rather than assumed, so a camera on another subnet
/// still works.
/// </para>
/// </remarks>
public static class CameraAddress
{
    /// <summary>The address every camera in this family uses.</summary>
    public static readonly IPAddress Default = IPAddress.Parse("192.168.100.1");

    /// <summary>
    /// The local address on the same /24 as <paramref name="camera"/>, or null when this machine is
    /// not on the camera's network.
    /// </summary>
    /// <param name="camera">The camera's address.</param>
    /// <param name="interfaces">The interface list; the real one when omitted.</param>
    public static IPAddress? LocalAddressFor(IPAddress camera, IEnumerable<IPAddress>? interfaces = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var cameraBytes = camera.GetAddressBytes();
        foreach (var candidate in interfaces ?? LocalIPv4Addresses())
        {
            if (candidate.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var bytes = candidate.GetAddressBytes();
            if (bytes[0] == cameraBytes[0] && bytes[1] == cameraBytes[1] && bytes[2] == cameraBytes[2]
                && !candidate.Equals(camera))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Every IPv4 address on an interface that is up.</summary>
    public static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return unicast.Address;
                }
            }
        }
    }
}
