using System.Buffers.Binary;
using Shouldly;
using Sightline.Platform.Windows.Wlan;
using Xunit;

namespace Sightline.Platform.Windows.Tests.Wlan;

/// <summary>Reading the Native Wi-Fi API's structures, from synthetic buffers.</summary>
public sealed class WlanLayoutTests
{
    private static readonly Guid Dongle = Guid.Parse("0d39fa57-97fd-49d4-85e8-d51333000001");
    private static readonly Guid BuiltIn = Guid.Parse("98d9443b-14e2-43cc-ba63-f0bad1000002");

    [Fact]
    public void An_interface_list_gives_each_adapters_id_hardware_and_state()
    {
        var rows = WlanLayout.Interfaces(NativeBuffers.InterfaceList(
            (BuiltIn, "Intel(R) Wi-Fi 6 AX201 160MHz", 1),
            (Dongle, "TP-Link Wireless MU-MIMO USB Adapter", 4)));

        rows.ShouldBe([
            new InterfaceRow(BuiltIn, "Intel(R) Wi-Fi 6 AX201 160MHz", 1),
            new InterfaceRow(Dongle, "TP-Link Wireless MU-MIMO USB Adapter", 4),
        ]);
    }

    [Fact]
    public void A_connection_gives_the_profile_the_network_and_the_signal()
    {
        var row = WlanLayout.Connection(NativeBuffers.Connection(1, "Tochi Galaxy 2", "Tochi Galaxy", 87));

        row.ShouldBe(new ConnectionRow(1, "Tochi Galaxy 2", "Tochi Galaxy", 87));
    }

    [Fact]
    public void A_connection_buffer_too_short_for_the_attributes_reads_as_none()
    {
        WlanLayout.Connection(new byte[WlanLayout.ConnectionSize - 1]).ShouldBeNull();
    }

    [Fact]
    public void A_network_list_gives_each_name_and_signal_with_names_read_as_utf8()
    {
        var rows = WlanLayout.Networks(NativeBuffers.NetworkList(("ActionCam_f8160c220c72", 100), ("Café ☕", 40), ("", 12)));

        rows.ShouldBe([new NetworkRow("ActionCam_f8160c220c72", 100), new NetworkRow("Café ☕", 40), new NetworkRow("", 12)]);
    }

    [Fact]
    public void A_count_from_native_memory_is_never_trusted_past_the_buffer()
    {
        var list = NativeBuffers.InterfaceList((Dongle, "one", 1));
        BinaryPrimitives.WriteUInt32LittleEndian(list, 1000);

        WlanLayout.Interfaces(list).Count.ShouldBe(1);
        WlanLayout.Count(new byte[4], WlanLayout.InterfaceSize).ShouldBe(0);
        WlanLayout.Networks([]).ShouldBeEmpty();
    }

    [Fact]
    public void A_network_name_longer_than_the_field_is_cut_at_32_bytes()
    {
        var field = NativeBuffers.Ssid(new string('x', 32));
        BinaryPrimitives.WriteUInt32LittleEndian(field, 200);
        var buffer = NativeBuffers.Connection(1, "p", "", 1);
        field.CopyTo(buffer, 520);

        WlanLayout.Connection(buffer)!.Ssid.ShouldBe(new string('x', 32));
    }

    [Fact]
    public void A_name_that_fills_its_field_with_no_terminator_is_still_read()
    {
        var buffer = NativeBuffers.Connection(1, new string('p', 256), "s", 1);

        WlanLayout.Connection(buffer)!.Profile.ShouldBe(new string('p', 256));
    }
}
