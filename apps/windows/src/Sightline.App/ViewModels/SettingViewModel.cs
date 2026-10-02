using CommunityToolkit.Mvvm.ComponentModel;
using Sightline.Protocol.GpSock;

namespace Sightline.App.ViewModels;

/// <summary>One of the camera's settings, as a row the user can change.</summary>
/// <remarks>
/// The options come from the camera's own menu, so the row can never offer a value the camera
/// would refuse. Changing the selection writes it straight to the camera.
/// </remarks>
public sealed partial class SettingViewModel : ObservableObject
{
    private readonly MenuSetting setting;
    private readonly MainViewModel owner;
    private bool loading = true;

    /// <summary>Creates a row for <paramref name="setting"/>, starting at the camera's default.</summary>
    public SettingViewModel(MenuSetting setting, MainViewModel owner)
    {
        this.setting = setting ?? throw new ArgumentNullException(nameof(setting));
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Choices = setting.Choices;
        selected = setting.Choices.FirstOrDefault(c => c.Value == setting.Default);
        loading = false;
    }

    /// <summary>The menu id the camera knows this setting by.</summary>
    public int Id => setting.Id;

    /// <summary>The setting's name.</summary>
    public string Name => setting.Name;

    /// <summary>The group it belongs to, such as Record or Capture.</summary>
    public string Category => setting.Category;

    /// <summary>What the camera will accept.</summary>
    public IReadOnlyList<MenuChoice> Choices { get; }

    /// <summary>The chosen value.</summary>
    [ObservableProperty]
    private MenuChoice selected;

    /// <summary>What the camera calls <paramref name="value"/>.</summary>
    public string LabelFor(int value) => setting.LabelFor(value);

    partial void OnSelectedChanged(MenuChoice value)
    {
        if (!loading)
        {
            _ = owner.WriteSettingAsync(this, value.Value);
        }
    }
}
