using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace Sightline.Platform.Windows.Wlan;

/// <summary>What Windows calls an adapter, and whether it is plugged in or built in.</summary>
internal interface IAdapterDirectory
{
    /// <summary>The adapter's name, such as <c>WiFi 2</c>, or null when it is gone.</summary>
    string? NameOf(Guid adapter);

    /// <summary>Whether the adapter is a USB device rather than part of the PC.</summary>
    bool IsExternal(Guid adapter);
}

/// <summary>
/// Adapter names from the network stack and buses from the device registry.
/// </summary>
/// <remarks>
/// A USB adapter's Plug and Play id starts <c>USB\</c>; a built-in one sits on PCI. That one fact
/// is what lets Sightline prefer the spare dongle to the adapter the PC normally lives on.
/// </remarks>
internal sealed class AdapterDirectory(
    Func<IEnumerable<(Guid Id, string Name)>> interfaces,
    Func<Guid, string?> plugAndPlayId) : IAdapterDirectory
{
    private const string NetworkClass = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    /// <summary>A directory over this PC's own network stack and registry.</summary>
    public AdapterDirectory()
        : this(() => InterfacesFrom(NetworkInterface.GetAllNetworkInterfaces()), RegistryPlugAndPlayId)
    {
    }

    /// <inheritdoc />
    public string? NameOf(Guid adapter) =>
        interfaces().Where(i => i.Id == adapter).Select(i => i.Name).FirstOrDefault();

    /// <inheritdoc />
    public bool IsExternal(Guid adapter) =>
        plugAndPlayId(adapter)?.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The interfaces Windows identifies by GUID; anything else is not an adapter Sightline can use.</summary>
    internal static IEnumerable<(Guid Id, string Name)> InterfacesFrom(IEnumerable<NetworkInterface> all) =>
        all.Where(n => Guid.TryParse(n.Id, out _)).Select(n => (Guid.Parse(n.Id), n.Name));

    private static string? RegistryPlugAndPlayId(Guid adapter)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{NetworkClass}\{adapter:B}\Connection");
        return key?.GetValue("PnPInstanceId") as string;
    }
}
