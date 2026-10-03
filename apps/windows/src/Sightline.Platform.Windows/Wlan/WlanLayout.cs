using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Sightline.Platform.Windows.Wlan;

/// <summary>An adapter as WlanEnumInterfaces lists it.</summary>
/// <param name="Id">The interface GUID.</param>
/// <param name="Description">The hardware's name.</param>
/// <param name="State">The WLAN_INTERFACE_STATE; <see cref="WlanLayout.Connected"/> when on a network.</param>
internal sealed record InterfaceRow(Guid Id, string Description, int State);

/// <summary>An adapter's current network, from WlanQueryInterface.</summary>
/// <param name="State">The WLAN_INTERFACE_STATE.</param>
/// <param name="Profile">The profile Windows joined with.</param>
/// <param name="Ssid">The network's name.</param>
/// <param name="SignalPercent">Signal quality, 0 to 100.</param>
internal sealed record ConnectionRow(int State, string Profile, string Ssid, int SignalPercent);

/// <summary>A network an adapter can see, from WlanGetAvailableNetworkList.</summary>
/// <param name="Ssid">The network's name; empty for a hidden network.</param>
/// <param name="SignalPercent">Signal quality, 0 to 100.</param>
internal sealed record NetworkRow(string Ssid, int SignalPercent);

/// <summary>
/// Reads the Native Wi-Fi API's structures out of the bytes it returns.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the calls themselves so every offset is tested against synthetic buffers, with
/// no Wi-Fi hardware involved. The layouts are wlanapi.h's, on 64-bit Windows; each offset below is
/// written out next to the field it reads so a reviewer can check it against the header.
/// </para>
/// <para>
/// Network names are bytes, not text, on the air. They are read as UTF-8, which is what every
/// camera in this family uses and what Windows itself assumes when it shows them.
/// </para>
/// </remarks>
internal static class WlanLayout
{
    /// <summary>wlan_interface_state_connected.</summary>
    public const int Connected = 1;

    /// <summary>Two DWORDs, count and index, before every list.</summary>
    public const int ListHeader = 8;

    /// <summary>WLAN_INTERFACE_INFO: GUID (16), WCHAR[256] (512), state (4).</summary>
    public const int InterfaceSize = 532;

    /// <summary>WLAN_AVAILABLE_NETWORK, as laid out on 64-bit Windows.</summary>
    public const int NetworkSize = 628;

    /// <summary>The WLAN_CONNECTION_ATTRIBUTES bytes this reads up to (the association attributes).</summary>
    public const int ConnectionSize = 604;

    private const int ProfileNameBytes = 512;

    /// <summary>The adapters in a WLAN_INTERFACE_INFO_LIST.</summary>
    public static IReadOnlyList<InterfaceRow> Interfaces(ReadOnlySpan<byte> list)
    {
        var rows = new List<InterfaceRow>();
        for (var i = 0; i < Count(list, InterfaceSize); i++)
        {
            var item = list.Slice(ListHeader + (i * InterfaceSize), InterfaceSize);
            rows.Add(new InterfaceRow(
                new Guid(item[..16]),                                           // InterfaceGuid
                WideString(item.Slice(16, ProfileNameBytes)),                   // strInterfaceDescription
                BinaryPrimitives.ReadInt32LittleEndian(item[528..])));          // isState
        }

        return rows;
    }

    /// <summary>The current network in a WLAN_CONNECTION_ATTRIBUTES, or null when the buffer is too short.</summary>
    public static ConnectionRow? Connection(ReadOnlySpan<byte> data)
    {
        if (data.Length < ConnectionSize)
        {
            return null;
        }

        return new ConnectionRow(
            BinaryPrimitives.ReadInt32LittleEndian(data),                       // isState
            WideString(data.Slice(8, ProfileNameBytes)),                        // strProfileName (after wlanConnectionMode)
            Ssid(data.Slice(520, 36)),                                          // wlanAssociationAttributes.dot11Ssid
            BinaryPrimitives.ReadInt32LittleEndian(data[576..]));               // .wlanSignalQuality (after the BSSID's padding)
    }

    /// <summary>The networks in a WLAN_AVAILABLE_NETWORK_LIST.</summary>
    public static IReadOnlyList<NetworkRow> Networks(ReadOnlySpan<byte> list)
    {
        var rows = new List<NetworkRow>();
        for (var i = 0; i < Count(list, NetworkSize); i++)
        {
            var item = list.Slice(ListHeader + (i * NetworkSize), NetworkSize);
            rows.Add(new NetworkRow(
                Ssid(item.Slice(512, 36)),                                      // dot11Ssid (after strProfileName)
                BinaryPrimitives.ReadInt32LittleEndian(item[604..])));          // wlanSignalQuality
        }

        return rows;
    }

    /// <summary>How many items a list holds, never more than its bytes can carry.</summary>
    /// <remarks>A count from native memory is not trusted further than the buffer that came with it.</remarks>
    public static int Count(ReadOnlySpan<byte> list, int itemSize)
    {
        if (list.Length < ListHeader)
        {
            return 0;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(list);
        var fits = (list.Length - ListHeader) / itemSize;
        return (int)Math.Min(declared, (uint)fits);
    }

    /// <summary>A DOT11_SSID: a ULONG length, then up to 32 bytes.</summary>
    private static string Ssid(ReadOnlySpan<byte> dot11Ssid)
    {
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(dot11Ssid), 32u);
        return Encoding.UTF8.GetString(dot11Ssid.Slice(4, length));
    }

    /// <summary>A NUL-terminated WCHAR array.</summary>
    private static string WideString(ReadOnlySpan<byte> bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes);
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }
}
