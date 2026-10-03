using System.Buffers.Binary;
using System.Text;
using Sightline.Platform.Windows.Wlan;

namespace Sightline.Platform.Windows.Tests.Wlan;

/// <summary>Builds the Native Wi-Fi API's structures byte by byte, laid out as wlanapi.h lays them out.</summary>
internal static class NativeBuffers
{
    public static byte[] InterfaceList(params (Guid Id, string Description, int State)[] items)
    {
        var list = new byte[WlanLayout.ListHeader + (items.Length * WlanLayout.InterfaceSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(list, (uint)items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = list.AsSpan(WlanLayout.ListHeader + (i * WlanLayout.InterfaceSize));
            items[i].Id.TryWriteBytes(item);
            Wide(items[i].Description).CopyTo(item[16..]);
            BinaryPrimitives.WriteInt32LittleEndian(item[528..], items[i].State);
        }

        return list;
    }

    public static byte[] Connection(int state, string profile, string ssid, int signal)
    {
        var data = new byte[WlanLayout.ConnectionSize];
        BinaryPrimitives.WriteInt32LittleEndian(data, state);
        Wide(profile).CopyTo(data.AsSpan(8));
        Ssid(ssid).CopyTo(data.AsSpan(520));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(576), signal);
        return data;
    }

    public static byte[] NetworkList(params (string Ssid, int Signal)[] items)
    {
        var list = new byte[WlanLayout.ListHeader + (items.Length * WlanLayout.NetworkSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(list, (uint)items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = list.AsSpan(WlanLayout.ListHeader + (i * WlanLayout.NetworkSize));
            Wide("profile that is not the ssid").CopyTo(item);
            Ssid(items[i].Ssid).CopyTo(item[512..]);
            BinaryPrimitives.WriteInt32LittleEndian(item[604..], items[i].Signal);
        }

        return list;
    }

    /// <summary>A DOT11_SSID: length, then the bytes, padded to 32.</summary>
    public static byte[] Ssid(string ssid)
    {
        var bytes = Encoding.UTF8.GetBytes(ssid);
        var field = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)bytes.Length);
        bytes.AsSpan(0, Math.Min(32, bytes.Length)).CopyTo(field.AsSpan(4));
        return field;
    }

    private static byte[] Wide(string text) => Encoding.Unicode.GetBytes(text + "\0");
}
