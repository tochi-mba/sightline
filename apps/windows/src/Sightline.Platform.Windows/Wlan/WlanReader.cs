using System.Runtime.InteropServices;

namespace Sightline.Platform.Windows.Wlan;

/// <summary>What Sightline reads from the Native Wi-Fi API. Read-only by construction.</summary>
internal interface IWlanReader
{
    /// <summary>Every Wi-Fi adapter. Empty when this PC has no Wi-Fi service at all.</summary>
    IReadOnlyList<InterfaceRow> Interfaces();

    /// <summary>The network <paramref name="adapter"/> is on, or null when it is on none.</summary>
    ConnectionRow? Connection(Guid adapter);

    /// <summary>The networks <paramref name="adapter"/> saw in its last scan.</summary>
    IReadOnlyList<NetworkRow> Networks(Guid adapter);

    /// <summary>Asks <paramref name="adapter"/> to scan. Changes nothing about its connection.</summary>
    void Scan(Guid adapter);
}

/// <summary>WlanOpenHandle.</summary>
internal delegate uint OpenHandleCall(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

/// <summary>WlanCloseHandle.</summary>
internal delegate uint CloseHandleCall(IntPtr clientHandle, IntPtr reserved);

/// <summary>WlanFreeMemory.</summary>
internal delegate void FreeMemoryCall(IntPtr memory);

/// <summary>WlanEnumInterfaces.</summary>
internal delegate uint EnumInterfacesCall(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

/// <summary>WlanQueryInterface.</summary>
internal delegate uint QueryInterfaceCall(
    IntPtr clientHandle, in Guid interfaceGuid, uint opCode, IntPtr reserved,
    out uint dataSize, out IntPtr data, out uint opcodeValueType);

/// <summary>WlanGetAvailableNetworkList.</summary>
internal delegate uint AvailableNetworksCall(IntPtr clientHandle, in Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr networkList);

/// <summary>WlanScan.</summary>
internal delegate uint ScanCall(IntPtr clientHandle, in Guid interfaceGuid, IntPtr ssid, IntPtr ieData, IntPtr reserved);

/// <summary>
/// The Native Wi-Fi entry points the reader uses.
/// </summary>
/// <remarks>
/// A seam at the very edge: tests hand in functions that return synthetic native memory, so every
/// path — no Wi-Fi service, a failed call, an adapter on no network — is exercised on any machine.
/// <see cref="Native"/> is the real thing, the operating system's own functions.
/// </remarks>
internal sealed record WlanFunctions(
    OpenHandleCall OpenHandle,
    CloseHandleCall CloseHandle,
    FreeMemoryCall FreeMemory,
    EnumInterfacesCall EnumInterfaces,
    QueryInterfaceCall QueryInterface,
    AvailableNetworksCall AvailableNetworks,
    ScanCall Scan)
{
    /// <summary>The operating system's functions.</summary>
    public static WlanFunctions Native { get; } = new(
        NativeWlan.WlanOpenHandle,
        NativeWlan.WlanCloseHandle,
        NativeWlan.WlanFreeMemory,
        NativeWlan.WlanEnumInterfaces,
        NativeWlan.WlanQueryInterface,
        NativeWlan.WlanGetAvailableNetworkList,
        NativeWlan.WlanScan);
}

/// <summary>
/// The Native Wi-Fi API's read side.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here can change a connection: Sightline's writes go through <see cref="NetshWlanWriter"/>,
/// a separate seam, so this can run for real in a test on any machine with no risk to a network
/// somebody is using.
/// </para>
/// <para>
/// Each call opens and closes its own handle. Sightline asks a handful of times a minute, and a
/// short-lived handle cannot leak or go stale when the Wi-Fi service restarts.
/// </para>
/// </remarks>
internal sealed class WlanReader(WlanFunctions functions) : IWlanReader
{
    private const uint ClientVersion = 2;
    private const uint Success = 0;
    private const uint CurrentConnection = 7;

    /// <summary>A reader over the operating system's own functions.</summary>
    public WlanReader()
        : this(WlanFunctions.Native)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<InterfaceRow> Interfaces() =>
        WithHandle(handle => functions.EnumInterfaces(handle, IntPtr.Zero, out var list) == Success
            ? WlanLayout.Interfaces(CopyList(list, WlanLayout.InterfaceSize))
            : []) ?? [];

    /// <inheritdoc />
    public ConnectionRow? Connection(Guid adapter) =>
        WithHandle(handle =>
        {
            // An adapter on no network answers ERROR_INVALID_STATE: an ordinary answer, not a failure.
            if (functions.QueryInterface(handle, in adapter, CurrentConnection, IntPtr.Zero, out var size, out var data, out _) != Success)
            {
                return null;
            }

            var row = WlanLayout.Connection(Copy(data, (int)size));
            return row is { State: WlanLayout.Connected } ? row : null;
        });

    /// <inheritdoc />
    public IReadOnlyList<NetworkRow> Networks(Guid adapter) =>
        WithHandle(handle => functions.AvailableNetworks(handle, in adapter, 0, IntPtr.Zero, out var list) == Success
            ? WlanLayout.Networks(CopyList(list, WlanLayout.NetworkSize))
            : []) ?? [];

    /// <inheritdoc />
    public void Scan(Guid adapter) =>
        WithHandle(handle => functions.Scan(handle, in adapter, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    /// <summary>Runs <paramref name="call"/> with an open handle, or returns default when there is no Wi-Fi service.</summary>
    private T? WithHandle<T>(Func<IntPtr, T> call)
    {
        if (functions.OpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != Success)
        {
            return default;
        }

        try
        {
            return call(handle);
        }
        finally
        {
            _ = functions.CloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>Copies a list the API allocated, sized by its own count, and frees it.</summary>
    private byte[] CopyList(IntPtr list, int itemSize) =>
        Copy(list, WlanLayout.ListHeader + (Marshal.ReadInt32(list) * itemSize));

    /// <summary>Copies memory the API allocated and frees it.</summary>
    private byte[] Copy(IntPtr memory, int length)
    {
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(memory, bytes, 0, length);
            return bytes;
        }
        finally
        {
            functions.FreeMemory(memory);
        }
    }
}

/// <summary>The operating system's Native Wi-Fi functions. Declarations only.</summary>
internal static partial class NativeWlan
{
    private const string Library = "wlanapi.dll";

    [LibraryImport(Library)]
    internal static partial uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [LibraryImport(Library)]
    internal static partial uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [LibraryImport(Library)]
    internal static partial void WlanFreeMemory(IntPtr memory);

    [LibraryImport(Library)]
    internal static partial uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [LibraryImport(Library)]
    internal static partial uint WlanQueryInterface(
        IntPtr clientHandle, in Guid interfaceGuid, uint opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out uint opcodeValueType);

    [LibraryImport(Library)]
    internal static partial uint WlanGetAvailableNetworkList(
        IntPtr clientHandle, in Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr networkList);

    [LibraryImport(Library)]
    internal static partial uint WlanScan(
        IntPtr clientHandle, in Guid interfaceGuid, IntPtr ssid, IntPtr ieData, IntPtr reserved);
}
