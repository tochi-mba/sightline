using System.Globalization;
using System.Security;
using System.Text;

namespace Sightline.Core.Connectivity;

/// <summary>
/// The Windows profile Sightline saves to join a camera.
/// </summary>
/// <remarks>
/// <para>
/// The profile has Sightline's own name, never the network's: a person who once joined the camera
/// by hand already has a profile called exactly the network's name, and saving over it — then
/// deleting it when done — would silently destroy their own saved network. An early build did
/// exactly that.
/// </para>
/// <para>
/// It joins only when asked (manual), so Windows never takes an adapter onto the camera by itself
/// later; and it carries the network name as hex as well as text, so a name with characters XML or
/// the local code page would mangle still matches the camera exactly.
/// </para>
/// </remarks>
public static class WlanProfile
{
    /// <summary>What every profile Sightline saves is called, before the network's name.</summary>
    public const string NamePrefix = "Sightline ";

    /// <summary>The profile name Sightline uses for <paramref name="ssid"/>.</summary>
    public static string NameFor(string ssid) => NamePrefix + ssid;

    /// <summary>Whether a profile name is one Sightline saved, and so one it may delete.</summary>
    public static bool IsOurs(string profileName) =>
        profileName is not null && profileName.StartsWith(NamePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="password"/> can be a Wi-Fi password at all: empty for an open
    /// network, 8 to 63 printable characters, or exactly 64 hex digits.
    /// </summary>
    public static bool IsValidPassword(string? password) =>
        string.IsNullOrEmpty(password)
        || (password.Length is >= 8 and <= 63 && password.All(c => c is >= ' ' and <= '~'))
        || (password.Length == 64 && password.All(Uri.IsHexDigit));

    /// <summary>The profile XML for joining <paramref name="ssid"/> with <paramref name="password"/>.</summary>
    /// <param name="ssid">The camera's network name.</param>
    /// <param name="password">Its WPA2 password, or empty for an open network.</param>
    /// <exception cref="ArgumentException">The password cannot be a Wi-Fi password.</exception>
    public static string Xml(string ssid, string? password)
    {
        ArgumentException.ThrowIfNullOrEmpty(ssid);
        if (!IsValidPassword(password))
        {
            throw new ArgumentException(
                "A Wi-Fi password is 8 to 63 characters (or 64 hex digits). The camera shows its own on its screen.",
                nameof(password));
        }

        var name = SecurityElement.Escape(NameFor(ssid));
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        var text = SecurityElement.Escape(ssid);
        var security = string.IsNullOrEmpty(password)
            ? "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>"
            : "<authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>"
              + string.Create(CultureInfo.InvariantCulture,
                  $"<sharedKey><keyType>{(password.Length == 64 ? "networkKey" : "passPhrase")}</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>");

        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig><SSID><hex>{hex}</hex><name>{text}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>manual</connectionMode>
              <MSM><security>{security}</security></MSM>
            </WLANProfile>
            """;
    }
}
