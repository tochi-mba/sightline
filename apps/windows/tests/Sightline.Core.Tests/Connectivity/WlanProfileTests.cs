using System.Xml.Linq;
using Shouldly;
using Sightline.Core.Connectivity;
using Xunit;

namespace Sightline.Core.Tests.Connectivity;

/// <summary>The profile Sightline saves to join a camera.</summary>
public sealed class WlanProfileTests
{
    private static readonly XNamespace Wlan = "http://www.microsoft.com/networking/WLAN/profile/v1";

    [Fact]
    public void The_profile_has_sightlines_own_name_never_the_networks()
    {
        // A person who once joined the camera by hand has a profile named exactly after it.
        // Saving over that, then deleting it when done, destroyed their own saved network.
        var xml = XDocument.Parse(WlanProfile.Xml("ActionCam_f8160c220c72", "12345678"));

        xml.Root!.Element(Wlan + "name")!.Value.ShouldBe("Sightline ActionCam_f8160c220c72");
        WlanProfile.NameFor("ActionCam_x").ShouldBe("Sightline ActionCam_x");
    }

    [Fact]
    public void Only_profiles_with_sightlines_name_are_ours()
    {
        WlanProfile.IsOurs("Sightline ActionCam_x").ShouldBeTrue();
        WlanProfile.IsOurs("ActionCam_x").ShouldBeFalse();
        WlanProfile.IsOurs("Tochi Galaxy 2").ShouldBeFalse();
        WlanProfile.IsOurs(null!).ShouldBeFalse();
    }

    [Fact]
    public void Windows_never_joins_the_camera_by_itself_from_this_profile()
    {
        var xml = XDocument.Parse(WlanProfile.Xml("ActionCam_x", "12345678"));

        xml.Root!.Element(Wlan + "connectionMode")!.Value.ShouldBe("manual");
    }

    [Fact]
    public void The_network_name_is_carried_as_hex_too_so_any_name_matches_exactly()
    {
        var xml = XDocument.Parse(WlanProfile.Xml("Cam & <Co> ☀", "12345678"));
        var ssid = xml.Root!.Element(Wlan + "SSIDConfig")!.Element(Wlan + "SSID")!;

        ssid.Element(Wlan + "name")!.Value.ShouldBe("Cam & <Co> ☀");
        Convert.FromHexString(ssid.Element(Wlan + "hex")!.Value)
            .ShouldBe(System.Text.Encoding.UTF8.GetBytes("Cam & <Co> ☀"));
    }

    [Fact]
    public void A_password_is_a_wpa2_passphrase()
    {
        var security = SecurityOf(WlanProfile.Xml("ActionCam_x", "pass<&>word"));

        security.Descendants(Wlan + "authentication").Single().Value.ShouldBe("WPA2PSK");
        security.Descendants(Wlan + "keyType").Single().Value.ShouldBe("passPhrase");
        security.Descendants(Wlan + "keyMaterial").Single().Value.ShouldBe("pass<&>word");
    }

    [Fact]
    public void Sixty_four_hex_digits_are_a_raw_key()
    {
        var key = new string('a', 64);

        SecurityOf(WlanProfile.Xml("ActionCam_x", key)).Descendants(Wlan + "keyType").Single().Value.ShouldBe("networkKey");
    }

    [Fact]
    public void No_password_is_an_open_network()
    {
        var security = SecurityOf(WlanProfile.Xml("OpenCam", ""));

        security.Descendants(Wlan + "authentication").Single().Value.ShouldBe("open");
        security.Descendants(Wlan + "sharedKey").ShouldBeEmpty();
        SecurityOf(WlanProfile.Xml("OpenCam", null)).Descendants(Wlan + "authentication").Single().Value.ShouldBe("open");
    }

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("a password with spaces", true)]
    [InlineData("tab\tinside", false)]
    [InlineData("ünïcode1", false)]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123", true)]
    [InlineData("012345678901234567890123456789012345678901234567890123456789012z", false)]
    [InlineData("", true)]
    public void A_password_is_checked_before_windows_ever_sees_it(string password, bool valid)
    {
        WlanProfile.IsValidPassword(password).ShouldBe(valid);
    }

    [Fact]
    public void A_password_that_cannot_be_one_is_refused_with_a_reason()
    {
        var refused = Should.Throw<ArgumentException>(() => WlanProfile.Xml("ActionCam_x", "short"));

        refused.Message.ShouldContain("8 to 63 characters");
        Should.Throw<ArgumentException>(() => WlanProfile.Xml("", "12345678"));
    }

    private static XElement SecurityOf(string xml) =>
        XDocument.Parse(xml).Root!.Element(Wlan + "MSM")!.Element(Wlan + "security")!;
}
