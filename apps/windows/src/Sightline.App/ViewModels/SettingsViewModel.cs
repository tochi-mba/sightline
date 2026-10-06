using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.Core.Camera;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;
using Sightline.Protocol.GpSock;

namespace Sightline.App.ViewModels;

/// <summary>One of the camera's own settings, as its menu describes it.</summary>
public sealed partial class CameraSettingItem : ObservableObject
{
    private readonly Action<int, int> change;
    private bool applying;

    internal CameraSettingItem(CameraSetting setting, Action<int, int> change)
    {
        this.change = change;
        Setting = setting;
        Apply(setting);
    }

    /// <summary>The setting, as the camera last reported it.</summary>
    public CameraSetting Setting { get; private set; }

    /// <summary>The camera's name for it.</summary>
    public string Name => Setting.Menu.Name;

    /// <summary>The group the camera files it under.</summary>
    public string Category => Setting.Menu.Category;

    /// <summary>What can be chosen.</summary>
    public IReadOnlyList<MenuChoice> Choices => Setting.Menu.Choices;

    /// <summary>Whether it can be changed here: choices only.</summary>
    public bool IsChangeable => Setting.IsChangeable;

    /// <summary>What it is set to, in the camera's words, or null when unknown.</summary>
    public string? Shown => Setting.Shown;

    /// <summary>The choice it is set to.</summary>
    [ObservableProperty]
    private MenuChoice? selected;

    /// <summary>Takes the camera's latest report.</summary>
    internal void Apply(CameraSetting setting)
    {
        Setting = setting;
        applying = true;
        Selected = setting.Value is { } value && setting.Menu.Choices.Any(c => c.Value == value)
            ? setting.Menu.Choices.First(c => c.Value == value)
            : null;
        applying = false;
        OnPropertyChanged(nameof(Shown));
    }

    partial void OnSelectedChanged(MenuChoice? value)
    {
        // A choice the camera reported is not a change somebody asked for.
        if (!applying && value is { } choice)
        {
            change(Setting.Menu.Id, choice.Value);
        }
    }
}

/// <summary>
/// The camera's own settings, read back from it, and the app's: each change saved as it is made.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Where the source lives, linked from About.</summary>
    public const string SourceUrl = "https://github.com/tochi-mba/sightline";

    private readonly AppParts parts;
    private readonly ShellViewModel shell;

    /// <summary>Creates the page.</summary>
    public SettingsViewModel(AppParts parts, ShellViewModel shell)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        parts.Preferences.Changed += OnPreferences;
    }

    /// <summary>The camera's settings that can be changed here, in the camera's own order.</summary>
    public ObservableCollection<CameraSettingItem> CameraSettings { get; } = [];

    /// <summary>The camera's settings shown but changed only on the camera itself.</summary>
    public ObservableCollection<CameraSettingItem> CameraFacts { get; } = [];

    /// <summary>Whether a camera is connected.</summary>
    [ObservableProperty]
    private bool cameraConnected;

    /// <summary>Why the camera's settings cannot be changed now, or null when they can.</summary>
    [ObservableProperty]
    private string? cameraLocked;

    /// <summary>The framing guides to choose from.</summary>
    public IReadOnlyList<GridOverlay> Grids { get; } = Enum.GetValues<GridOverlay>();

    /// <summary>The sensitivities to choose from.</summary>
    public IReadOnlyList<Sensitivity> Sensitivities { get; } = Enum.GetValues<Sensitivity>();

    /// <summary>The arming delays to choose from, in seconds: 0 to 120, in fives.</summary>
    public IReadOnlyList<int> ArmDelays { get; } = Enumerable.Range(0, 25).Select(i => i * 5).ToList();

    /// <summary>The waits between alarms to choose from, in seconds: 10 to 600, in tens.</summary>
    public IReadOnlyList<int> Cooldowns { get; } = Enumerable.Range(1, 60).Select(i => i * 10).ToList();

    /// <summary>This build's version.</summary>
    public string Version => parts.Version;

    /// <inheritdoc cref="Preferences.AutoConnect"/>
    public bool AutoConnect { get => Current.AutoConnect; set => Save(p => p with { AutoConnect = value }); }

    /// <inheritdoc cref="Preferences.Reconnect"/>
    public bool Reconnect { get => Current.Reconnect; set => Save(p => p with { Reconnect = value }); }

    /// <summary>Whether the picture covers the area, cropped, rather than fits inside it.</summary>
    public bool FillPicture { get => Current.Fit == PictureFit.Fill; set => Save(p => p with { Fit = value ? PictureFit.Fill : PictureFit.Fit }); }

    /// <inheritdoc cref="Preferences.Grid"/>
    public GridOverlay Grid { get => Current.Grid; set => Save(p => p with { Grid = value }); }

    /// <inheritdoc cref="Preferences.Flip"/>
    public bool Flip { get => Current.Flip; set => Save(p => p with { Flip = value }); }

    /// <inheritdoc cref="Preferences.Mirror"/>
    public bool Mirror { get => Current.Mirror; set => Save(p => p with { Mirror = value }); }

    /// <inheritdoc cref="Preferences.ShowFrameRate"/>
    public bool ShowFrameRate { get => Current.ShowFrameRate; set => Save(p => p with { ShowFrameRate = value }); }

    /// <summary>Where copies from the card go.</summary>
    public string DownloadFolder
    {
        get => Current.DownloadFolder ?? FolderSink.DefaultFolder;
        set => Save(p => p with { DownloadFolder = value == FolderSink.DefaultFolder ? null : value });
    }

    /// <inheritdoc cref="Preferences.DeleteAfterCopy"/>
    public bool DeleteAfterCopy { get => Current.DeleteAfterCopy; set => Save(p => p with { DeleteAfterCopy = value }); }

    /// <inheritdoc cref="Preferences.SentrySensitivity"/>
    public Sensitivity SentrySensitivity { get => Current.SentrySensitivity; set => Save(p => p with { SentrySensitivity = value }); }

    /// <inheritdoc cref="Preferences.SentryArmDelaySeconds"/>
    public int SentryArmDelay { get => Current.SentryArmDelaySeconds; set => Save(p => p with { SentryArmDelaySeconds = value }); }

    /// <inheritdoc cref="Preferences.SentryCooldownSeconds"/>
    public int SentryCooldown { get => Current.SentryCooldownSeconds; set => Save(p => p with { SentryCooldownSeconds = value }); }

    /// <inheritdoc cref="Preferences.SentryRecords"/>
    public bool SentryRecords { get => Current.SentryRecords; set => Save(p => p with { SentryRecords = value }); }

    /// <inheritdoc cref="Preferences.SentrySnapshots"/>
    public bool SentrySnapshots { get => Current.SentrySnapshots; set => Save(p => p with { SentrySnapshots = value }); }

    /// <inheritdoc cref="Preferences.CheckForUpdates"/>
    public bool CheckForUpdates { get => Current.CheckForUpdates; set => Save(p => p with { CheckForUpdates = value }); }

    /// <summary>Newer versions of Sightline.</summary>
    public UpdatesViewModel Updates => shell.Updates;

    /// <inheritdoc cref="Preferences.CloseToTray"/>
    public bool CloseToTray { get => Current.CloseToTray; set => Save(p => p with { CloseToTray = value }); }

    private Preferences Current => parts.Preferences.Current;

    /// <inheritdoc />
    public void Dispose() => parts.Preferences.Changed -= OnPreferences;

    /// <summary>Follows the camera's settings, and whether they can change now.</summary>
    internal void Apply(CameraState state)
    {
        CameraConnected = state.IsConnected;
        CameraLocked = !state.IsConnected ? "Connect a camera to change its own settings."
            : state.IsRecording ? "The camera's settings are locked while it records."
            : null;
        Sync(CameraSettings, state.IsConnected ? state.Settings.Where(s => s.IsChangeable) : []);
        Sync(CameraFacts, state.IsConnected ? state.Settings.Where(s => !s.IsChangeable) : []);
    }

    private void Sync(ObservableCollection<CameraSettingItem> items, IEnumerable<CameraSetting> settings)
    {
        var latest = settings.ToList();
        if (items.Select(i => i.Setting.Menu.Id).SequenceEqual(latest.Select(s => s.Menu.Id)))
        {
            for (var i = 0; i < latest.Count; i++)
            {
                items[i].Apply(latest[i]);
            }

            return;
        }

        items.Clear();
        foreach (var setting in latest)
        {
            items.Add(new CameraSettingItem(setting, (id, value) => parts.Controller.ChangeSetting(id, value)));
        }
    }

    private void Save(Func<Preferences, Preferences> change)
    {
        try
        {
            parts.Preferences.Update(change);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The change still holds for this run; it simply was not kept for the next.
        }
    }

    private void OnPreferences(Preferences preferences) => parts.Post(() => OnPropertyChanged(string.Empty));

    [RelayCommand]
    private void OpenSource() => parts.Desktop.OpenLink(SourceUrl);

    [RelayCommand]
    private void OpenDownloads() => parts.Desktop.OpenFolder(DownloadFolder);

    [RelayCommand]
    private void ShowIntroduction() => shell.ShowIntroduction();
}
