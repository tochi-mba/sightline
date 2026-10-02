using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The settings catalogue, against the menu the reference camera actually sent.
/// </summary>
/// <remarks>
/// <c>golden/menu/reference-camera.xml</c> is 18,523 bytes read off the camera over GPSOCKET on
/// 2026-10-02 (firmware 20240708 V1.3). Parsing the real document is what proves the app will show
/// the settings this camera has, rather than a table someone typed out of a manual.
/// </remarks>
public sealed class MenuCatalogTests
{
    private static MenuCatalog Reference() =>
        MenuCatalog.Parse(File.ReadAllText(Path.Combine("golden", "menu", "reference-camera.xml")));

    [Fact]
    public void The_reference_camera_menu_parses()
    {
        var catalogue = Reference();

        catalogue.Settings.Count.ShouldBe(21);
    }

    [Fact]
    public void It_finds_the_four_groups_the_camera_files_its_settings_under()
    {
        Reference().Categories.ShouldBe(["Record", "Capture", "System", "Wifi"]);
    }

    [Fact]
    public void Record_resolution_offers_everything_up_to_4K()
    {
        var resolution = Reference().Find(0x0000);

        resolution.ShouldNotBeNull();
        resolution.Name.ShouldBe("Resolution");
        resolution.Category.ShouldBe("Record");
        resolution.Kind.ShouldBe(MenuSettingKind.Choice);
        resolution.Choices.Select(c => c.Label).ShouldContain("4K");
        resolution.LabelFor(0).ShouldBe("4K");
    }

    [Fact]
    public void Capture_resolution_is_a_different_setting_from_record_resolution()
    {
        // Both are called "Resolution"; only the id tells them apart, which is why the app keys
        // everything on the id and never on the name.
        var capture = Reference().Find(0x0100);

        capture.ShouldNotBeNull();
        capture.Category.ShouldBe("Capture");
        capture.Choices.Select(c => c.Label).ShouldContain(label => label.StartsWith("16M", StringComparison.Ordinal));
    }

    [Fact]
    public void An_action_is_not_offered_as_a_value_to_choose()
    {
        var format = Reference().Find(0x0207);

        format.ShouldNotBeNull();
        format.Name.ShouldBe("Format");
        format.Kind.ShouldBe(MenuSettingKind.Action);
        format.Choices.ShouldBeEmpty();
    }

    [Fact]
    public void The_version_the_camera_reports_is_read_only()
    {
        var version = Reference().Find(0x0209);

        version.ShouldNotBeNull();
        version.Kind.ShouldBe(MenuSettingKind.ReadOnly);
        version.IsWritable.ShouldBeFalse();
    }

    [Fact]
    public void The_wifi_name_is_text_rather_than_a_choice()
    {
        var name = Reference().Find(0x0300);

        name.ShouldNotBeNull();
        name.Kind.ShouldBe(MenuSettingKind.Text);
        name.IsWritable.ShouldBeTrue();
    }

    [Fact]
    public void A_value_the_camera_never_offered_is_shown_as_itself_rather_than_guessed_at()
    {
        Reference().Find(0x0000)!.LabelFor(99).ShouldBe("99");
    }

    [Fact]
    public void Padding_after_the_final_tag_does_not_stop_it_parsing()
    {
        // The camera pads the last chunk to its buffer size, so a real document has rubbish on the
        // end. Trimming to the closing tag is what makes it well-formed.
        var padded = File.ReadAllText(Path.Combine("golden", "menu", "reference-camera.xml"))
            + "\0\0\0\0rubbish";

        MenuCatalog.Parse(padded).Settings.Count.ShouldBe(21);
    }

    [Fact]
    public void A_document_with_no_closing_tag_is_refused_rather_than_half_read()
    {
        Should.Throw<GpSockProtocolException>(() => MenuCatalog.Parse("<Menu><Categories>"));
    }

    [Fact]
    public void A_menu_listing_nothing_is_refused()
    {
        Should.Throw<GpSockProtocolException>(() => MenuCatalog.Parse("<Menu></Menu>"));
    }

    [Fact]
    public void A_setting_with_no_id_is_left_out_rather_than_given_a_made_up_one()
    {
        var xml = """
            <Menu><Categories><Category><Name>Record</Name><Settings>
              <Setting><Name>Good</Name><ID>0x0001</ID><Type>0x00</Type><Default>0x00</Default></Setting>
              <Setting><Name>Nameless but present</Name><Type>0x00</Type></Setting>
            </Settings></Category></Categories></Menu>
            """;

        var catalogue = MenuCatalog.Parse(xml);

        catalogue.Settings.Count.ShouldBe(1);
        catalogue.Settings[0].Name.ShouldBe("Good");
    }
}
