using System.Runtime.InteropServices;
using Shouldly;
using Sightline.Platform.Windows.Wlan;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Wlan;

/// <summary>
/// The Native Wi-Fi reader, against functions that hand back real unmanaged memory as Windows does.
/// </summary>
public sealed class WlanReaderTests
{
    private static readonly Guid Dongle = Guid.Parse("0d39fa57-97fd-49d4-85e8-d51333000001");
    private const uint Success = 0;
    private const uint InvalidState = 5023;
    private const uint ServiceNotActive = 1062;

    [Fact]
    public void Adapters_are_read_and_the_memory_windows_allocated_is_freed()
    {
        var native = new FakeNative { InterfaceList = NativeBuffers.InterfaceList((Dongle, "TP-Link", 1)) };

        new WlanReader(native.Functions).Interfaces().ShouldBe([new InterfaceRow(Dongle, "TP-Link", 1)]);

        native.Outstanding.ShouldBe(0);
        native.HandlesClosed.ShouldBe(1);
    }

    [Fact]
    public void A_pc_with_no_wifi_service_has_no_adapters_rather_than_an_error()
    {
        var native = new FakeNative { OpenResult = ServiceNotActive };
        var reader = new WlanReader(native.Functions);

        reader.Interfaces().ShouldBeEmpty();
        reader.Networks(Dongle).ShouldBeEmpty();
        reader.Connection(Dongle).ShouldBeNull();
        native.HandlesClosed.ShouldBe(0);
    }

    [Fact]
    public void A_failed_listing_is_an_empty_one()
    {
        var native = new FakeNative { EnumResult = 87, NetworksResult = 87 };
        var reader = new WlanReader(native.Functions);

        reader.Interfaces().ShouldBeEmpty();
        reader.Networks(Dongle).ShouldBeEmpty();
        native.HandlesClosed.ShouldBe(2);
    }

    [Fact]
    public void The_current_network_is_read_from_the_connection_attributes()
    {
        var native = new FakeNative { ConnectionData = NativeBuffers.Connection(1, "Sightline ActionCam_x", "ActionCam_x", 100) };

        new WlanReader(native.Functions).Connection(Dongle).ShouldBe(new ConnectionRow(1, "Sightline ActionCam_x", "ActionCam_x", 100));
        native.Outstanding.ShouldBe(0);
        native.QueriedFor.ShouldBe(Dongle);
    }

    [Fact]
    public void An_adapter_on_no_network_answers_invalid_state_which_reads_as_none()
    {
        var native = new FakeNative { QueryResult = InvalidState };

        new WlanReader(native.Functions).Connection(Dongle).ShouldBeNull();
    }

    [Fact]
    public void Attributes_saying_not_connected_read_as_none()
    {
        var native = new FakeNative { ConnectionData = NativeBuffers.Connection(4, "", "", 0) };

        new WlanReader(native.Functions).Connection(Dongle).ShouldBeNull();
    }

    [Fact]
    public void Visible_networks_are_read_and_freed()
    {
        var native = new FakeNative { NetworkList = NativeBuffers.NetworkList(("ActionCam_x", 99)) };

        new WlanReader(native.Functions).Networks(Dongle).ShouldBe([new NetworkRow("ActionCam_x", 99)]);
        native.Outstanding.ShouldBe(0);
    }

    [Fact]
    public void A_scan_is_asked_of_the_named_adapter()
    {
        var native = new FakeNative();

        new WlanReader(native.Functions).Scan(Dongle);

        native.ScannedFor.ShouldBe(Dongle);
        native.HandlesClosed.ShouldBe(1);
    }

    [Fact]
    public void The_real_reader_answers_on_this_machine_without_changing_anything()
    {
        // Read-only: listing adapters and asking about a made-up one cannot affect any network.
        var reader = new WlanReader();

        var adapters = reader.Interfaces();
        reader.Connection(Guid.NewGuid()).ShouldBeNull();
        reader.Networks(Guid.NewGuid()).ShouldBeEmpty();

        adapters.ShouldAllBe(a => a.Id != Guid.Empty);
        WlanFunctions.Native.ShouldNotBeNull();
    }

    /// <summary>Native functions backed by unmanaged memory the test can account for.</summary>
    private sealed class FakeNative
    {
        private readonly HashSet<IntPtr> allocated = [];

        public uint OpenResult { get; init; } = Success;

        public uint EnumResult { get; init; } = Success;

        public uint QueryResult { get; init; } = Success;

        public uint NetworksResult { get; init; } = Success;

        public byte[] InterfaceList { get; init; } = NativeBuffers.InterfaceList();

        public byte[] ConnectionData { get; init; } = NativeBuffers.Connection(1, "p", "s", 1);

        public byte[] NetworkList { get; init; } = NativeBuffers.NetworkList();

        public int HandlesClosed { get; private set; }

        public Guid? QueriedFor { get; private set; }

        public Guid? ScannedFor { get; private set; }

        public int Outstanding => allocated.Count;

        public WlanFunctions Functions => new(
            (uint version, IntPtr reserved, out uint negotiated, out IntPtr handle) =>
            {
                negotiated = version;
                handle = (IntPtr)42;
                return OpenResult;
            },
            (handle, reserved) =>
            {
                HandlesClosed++;
                return Success;
            },
            memory =>
            {
                allocated.Remove(memory).ShouldBeTrue();
                Marshal.FreeHGlobal(memory);
            },
            (IntPtr handle, IntPtr reserved, out IntPtr list) =>
            {
                list = Allocate(InterfaceList);
                return EnumResult;
            },
            (IntPtr handle, in Guid adapter, uint opCode, IntPtr reserved, out uint size, out IntPtr data, out uint type) =>
            {
                QueriedFor = adapter;
                opCode.ShouldBe(7u);
                size = (uint)ConnectionData.Length;
                data = QueryResult == Success ? Allocate(ConnectionData) : IntPtr.Zero;
                type = 0;
                return QueryResult;
            },
            (IntPtr handle, in Guid adapter, uint flags, IntPtr reserved, out IntPtr list) =>
            {
                list = NetworksResult == Success ? Allocate(NetworkList) : IntPtr.Zero;
                return NetworksResult;
            },
            (IntPtr handle, in Guid adapter, IntPtr ssid, IntPtr ie, IntPtr reserved) =>
            {
                ScannedFor = adapter;
                return Success;
            });

        private IntPtr Allocate(byte[] bytes)
        {
            if (EnumResult != Success && ReferenceEquals(bytes, InterfaceList))
            {
                return IntPtr.Zero;
            }

            var memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            allocated.Add(memory);
            return memory;
        }
    }
}
