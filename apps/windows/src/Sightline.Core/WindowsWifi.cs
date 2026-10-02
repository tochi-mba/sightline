using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace Sightline.Core;

/// <summary>A Wi-Fi adapter on this PC, as Windows reports it.</summary>
/// <param name="Name">The interface name, such as <c>WiFi 2</c>.</param>
/// <param name="Description">The hardware, such as <c>TP-Link Wireless MU-MIMO USB Adapter</c>.</param>
/// <param name="ConnectedTo">The network it is on, or null when it is idle.</param>
public sealed record WifiAdapter(string Name, string Description, string? ConnectedTo)
{
    /// <summary>Whether nothing is using it, which makes it the right one for the camera.</summary>
    public bool IsIdle => ConnectedTo is null;
}

/// <summary>
/// Joins and leaves the camera's Wi-Fi on Windows, through <c>netsh</c>.
/// </summary>
/// <remarks>
/// <para>
/// The adapter matters more than anything else here. A PC whose only Wi-Fi is also its internet
/// loses that internet while it is on the camera, so the idle adapter is always preferred, and the
/// one carrying internet is used only when nothing else exists — and the user is told first.
/// </para>
/// <para>
/// The profile written is temporary: it is removed again when the session ends, so the camera does
/// not become a network Windows joins on its own later.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsWifi
{
    /// <summary>Every Wi-Fi adapter, and what each is connected to.</summary>
    public static IReadOnlyList<WifiAdapter> Adapters() => ParseInterfaces(Run("wlan show interfaces"));

    /// <summary>
    /// Reads <c>netsh wlan show interfaces</c> output.
    /// </summary>
    /// <remarks>Public so the parsing is tested against real captured output.</remarks>
    public static IReadOnlyList<WifiAdapter> ParseInterfaces(string output)
    {
        var adapters = new List<WifiAdapter>();
        string? name = null;
        string? description = null;
        string? state = null;
        string? ssid = null;

        void Flush()
        {
            if (name is not null)
            {
                var connected = state?.Equals("connected", StringComparison.OrdinalIgnoreCase) == true ? ssid : null;
                adapters.Add(new WifiAdapter(name, description ?? "", connected));
            }

            name = description = state = ssid = null;
        }

        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "Name":
                    Flush();
                    name = value;
                    break;
                case "Description":
                    description = value;
                    break;
                case "State":
                    state = value;
                    break;
                case "SSID":
                    ssid = value;
                    break;
            }
        }

        Flush();
        return adapters;
    }

    /// <summary>
    /// Chooses the adapter to put on the camera: an idle one when there is one.
    /// </summary>
    /// <returns>The adapter, and whether using it will interrupt this PC's internet.</returns>
    public static (WifiAdapter Adapter, bool InterruptsInternet)? Choose(IReadOnlyList<WifiAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var idle = adapters.FirstOrDefault(a => a.IsIdle);
        if (idle is not null)
        {
            return (idle, false);
        }

        return adapters.Count == 0 ? null : (adapters[0], true);
    }

    /// <summary>Puts <paramref name="adapter"/> on the camera's network.</summary>
    /// <param name="adapter">The interface name.</param>
    /// <param name="ssid">The camera's network name.</param>
    /// <param name="password">The camera's Wi-Fi password.</param>
    /// <returns>netsh's own words, for showing when something goes wrong.</returns>
    public static string Join(string adapter, string ssid, string password)
    {
        var profile = Path.Combine(Path.GetTempPath(), $"sightline-{Guid.NewGuid():N}.xml");
        File.WriteAllText(profile, ProfileXml(ssid, password), Encoding.UTF8);
        try
        {
            var added = Run($"wlan add profile filename=\"{profile}\" interface=\"{adapter}\"");
            var connected = Run($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\" interface=\"{adapter}\"");
            return $"{added.Trim()}\n{connected.Trim()}";
        }
        finally
        {
            // The password is in this file, so it does not outlive the join.
            File.Delete(profile);
        }
    }

    /// <summary>Takes <paramref name="adapter"/> off the camera and forgets the temporary profile.</summary>
    public static void Leave(string adapter, string ssid)
    {
        Run($"wlan disconnect interface=\"{adapter}\"");
        Run($"wlan delete profile name=\"{ssid}\" interface=\"{adapter}\"");
    }

    /// <summary>The WLAN profile Windows needs to join a WPA2 network.</summary>
    public static string ProfileXml(string ssid, string password)
    {
        var name = SecurityElement.Escape(ssid);
        var key = SecurityElement.Escape(password);
        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>manual</connectionMode>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{key}</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;
    }

    private static string Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // A neighbouring network with an emoji in its name is not a reason for this to fail.
            StandardOutputEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException("netsh could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
