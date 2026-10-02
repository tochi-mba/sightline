using System.Globalization;
using System.Xml.Linq;

namespace Sightline.Protocol.GpSock;

/// <summary>What kind of setting a menu entry is, from its <c>Type</c> field.</summary>
public enum MenuSettingKind
{
    /// <summary>A choice from a fixed list. Nearly everything is one of these.</summary>
    Choice = 0x00,

    /// <summary>An action rather than a value: formatting the card, restoring defaults.</summary>
    Action = 0x01,

    /// <summary>Free text, such as the Wi-Fi name and password.</summary>
    Text = 0x02,

    /// <summary>Something the camera reports and will not let you change, such as its version.</summary>
    ReadOnly = 0x03,
}

/// <summary>One value a <see cref="MenuSetting"/> will accept.</summary>
/// <param name="Value">The number written to the camera.</param>
/// <param name="Label">What the camera calls it, in the language the menu was fetched in.</param>
public readonly record struct MenuChoice(int Value, string Label);

/// <summary>One setting the camera supports.</summary>
/// <param name="Id">The menu id, as <see cref="GpSockCommand.MenuSetParameter"/> takes it.</param>
/// <param name="Name">The camera's own name for it.</param>
/// <param name="Category">The group the camera files it under.</param>
/// <param name="Kind">Whether it is a choice, an action, text, or read-only.</param>
/// <param name="Default">The camera's default value.</param>
/// <param name="Choices">The values it will accept, empty for a non-choice setting.</param>
public sealed record MenuSetting(
    int Id,
    string Name,
    string Category,
    MenuSettingKind Kind,
    int Default,
    IReadOnlyList<MenuChoice> Choices)
{
    /// <summary>Whether this setting can be written at all.</summary>
    public bool IsWritable => Kind is MenuSettingKind.Choice or MenuSettingKind.Text;

    /// <summary>What the camera calls <paramref name="value"/>, or the number when it has no name.</summary>
    public string LabelFor(int value)
    {
        foreach (var choice in Choices)
        {
            if (choice.Value == value)
            {
                return choice.Label;
            }
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Every setting a camera supports, read from the camera rather than hardcoded.
/// </summary>
/// <remarks>
/// The camera answers <see cref="GpSockCommand.GetParameterFile"/> with its own menu as XML. Taking
/// the catalogue from there rather than from a table in this app is what lets a camera with a
/// different firmware, a different language, or a feature this one does not have still show the
/// right settings with the right options — and what stops the app offering a resolution the camera
/// would refuse.
/// </remarks>
public sealed class MenuCatalog
{
    private readonly List<MenuSetting> settings;

    private MenuCatalog(List<MenuSetting> settings)
    {
        this.settings = settings;
    }

    /// <summary>Every setting, in the order the camera listed them.</summary>
    public IReadOnlyList<MenuSetting> Settings => settings;

    /// <summary>The category names, in the camera's order.</summary>
    public IReadOnlyList<string> Categories =>
        settings.Select(s => s.Category).Distinct().ToList();

    /// <summary>The setting with this id, or <see langword="null"/> when the camera has no such setting.</summary>
    public MenuSetting? Find(int id) => settings.FirstOrDefault(s => s.Id == id);

    /// <summary>Reads a catalogue from the camera's menu XML.</summary>
    /// <param name="xml">The bytes the camera sent, which may carry padding after the final tag.</param>
    /// <exception cref="GpSockProtocolException">The bytes are not a menu this app can read.</exception>
    public static MenuCatalog Parse(ReadOnlySpan<byte> xml) =>
        Parse(System.Text.Encoding.UTF8.GetString(xml));

    /// <summary>Reads a catalogue from the camera's menu XML.</summary>
    /// <param name="xml">The document, which may carry padding after the final tag.</param>
    /// <exception cref="GpSockProtocolException">The text is not a menu this app can read.</exception>
    public static MenuCatalog Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        // The last chunk is padded to the camera's buffer size, so there is usually rubbish after
        // the closing tag. Trimming to it is what makes the document well-formed.
        var end = xml.LastIndexOf("</Menu>", StringComparison.Ordinal);
        if (end < 0)
        {
            throw new GpSockProtocolException("The menu XML has no closing </Menu> tag.");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml[..(end + "</Menu>".Length)]);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new GpSockProtocolException("The camera's menu is not well-formed XML.", exception);
        }

        var parsed = new List<MenuSetting>();
        foreach (var category in document.Descendants("Category"))
        {
            var categoryName = Text(category.Element("Name")) ?? "Other";
            foreach (var setting in category.Descendants("Setting"))
            {
                var id = Number(Text(setting.Element("ID")));
                var name = Text(setting.Element("Name"));
                if (id is null || name is null)
                {
                    // A setting with no id cannot be written and a setting with no name cannot be
                    // shown, so there is nothing useful to do with it except leave it out.
                    continue;
                }

                var choices = setting.Descendants("Value")
                    .Select(value => (Id: Number(Text(value.Element("ID"))), Label: Text(value.Element("Name"))))
                    .Where(pair => pair.Id is not null && pair.Label is not null)
                    .Select(pair => new MenuChoice(pair.Id!.Value, pair.Label!))
                    .ToList();

                parsed.Add(new MenuSetting(
                    id.Value,
                    name,
                    categoryName,
                    (MenuSettingKind)(Number(Text(setting.Element("Type"))) ?? 0),
                    Number(Text(setting.Element("Default"))) ?? 0,
                    choices));
            }
        }

        if (parsed.Count == 0)
        {
            throw new GpSockProtocolException("The camera's menu listed no settings.");
        }

        return new MenuCatalog(parsed);
    }

    private static string? Text(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Reads the camera's numbers, which it writes as <c>0x0000100</c> or plainly.</summary>
    private static int? Number(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)
            ? plain
            : null;
    }
}
