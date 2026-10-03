using Sightline.Core.Connectivity;
using Sightline.Platform.Windows.Processes;

namespace Sightline.Platform.Windows.Wlan;

/// <summary>
/// The four things Sightline ever changes about Wi-Fi, through netsh.
/// </summary>
/// <remarks>
/// <para>
/// Only the exit code is read, never the text: netsh's messages are translated, so parsing them
/// would work on an English Windows and fail on every other. What the change did is then read back
/// through the Native Wi-Fi API, which answers in structures.
/// </para>
/// <para>
/// Every argument is quoted, and a value carrying a quote — which netsh cannot express — is refused
/// before anything runs, so no network name can smuggle in an argument of its own.
/// </para>
/// </remarks>
internal sealed class NetshWlanWriter(ICommandRunner runner)
{
    /// <summary>Saves a profile on the named adapter, replacing one of the same name.</summary>
    /// <remarks>The XML holds the Wi-Fi password, so the file it passes through is deleted at once.</remarks>
    public void AddProfile(string adapter, string profileXml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sightline-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, profileXml);
        try
        {
            Run($"wlan add profile filename={Quote(file)} interface={Quote(adapter)} user=current");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Asks the named adapter to join <paramref name="ssid"/> with <paramref name="profile"/>.</summary>
    public void Connect(string adapter, string profile, string ssid) =>
        Run($"wlan connect name={Quote(profile)} ssid={Quote(ssid)} interface={Quote(adapter)}");

    /// <summary>Takes the named adapter off its network.</summary>
    public void Disconnect(string adapter) => Run($"wlan disconnect interface={Quote(adapter)}");

    /// <summary>Deletes <paramref name="profile"/> from the named adapter.</summary>
    public void DeleteProfile(string adapter, string profile) =>
        Run($"wlan delete profile name={Quote(profile)} interface={Quote(adapter)}");

    private void Run(string arguments)
    {
        var result = runner.Run("netsh", arguments);
        if (result.ExitCode != 0)
        {
            throw new WlanException($"Windows refused: {result.Output.Trim()}");
        }
    }

    private static string Quote(string value) =>
        value.Contains('"', StringComparison.Ordinal)
            ? throw new WlanException($"Windows' Wi-Fi tool cannot take a name containing a quote: {value}")
            : $"\"{value}\"";
}
