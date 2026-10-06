using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Camera;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;

namespace Sightline.App.Tests.ViewModels;

/// <summary>The camera's own settings and the app's.</summary>
public sealed class SettingsViewModelTests
{
    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.2.0" };

    [AvaloniaFact]
    public async Task Every_app_setting_is_saved_as_it_changes()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var settings = shell.Settings;
        var changed = 0;
        ((INotifyPropertyChanged)settings).PropertyChanged += (_, _) => changed++;

        settings.AutoConnect = false;
        settings.Reconnect = false;
        settings.FillPicture = true;
        settings.Grid = GridOverlay.Centre;
        settings.Flip = true;
        settings.Mirror = true;
        settings.ShowFrameRate = true;
        settings.DeleteAfterCopy = true;
        settings.SentrySensitivity = Sensitivity.Low;
        settings.SentryArmDelay = 30;
        settings.SentryCooldown = 60;
        settings.SentryRecords = false;
        settings.SentrySnapshots = false;
        settings.CloseToTray = false;
        settings.FillPicture = false;
        settings.FillPicture = true;

        var saved = new PreferencesStore(app.Preferences.Path).Current;
        saved.ShouldBe(Returning with
        {
            AutoConnect = false,
            Reconnect = false,
            Fit = PictureFit.Fill,
            Grid = GridOverlay.Centre,
            Flip = true,
            Mirror = true,
            ShowFrameRate = true,
            DeleteAfterCopy = true,
            SentrySensitivity = Sensitivity.Low,
            SentryArmDelaySeconds = 30,
            SentryCooldownSeconds = 60,
            SentryRecords = false,
            SentrySnapshots = false,
            CloseToTray = false,
        });
        (settings.AutoConnect, settings.Reconnect, settings.FillPicture, settings.Grid, settings.Flip, settings.Mirror)
            .ShouldBe((false, false, true, GridOverlay.Centre, true, true));
        (settings.ShowFrameRate, settings.DeleteAfterCopy, settings.SentrySensitivity, settings.SentryArmDelay, settings.SentryCooldown)
            .ShouldBe((true, true, Sensitivity.Low, 30, 60));
        (settings.SentryRecords, settings.SentrySnapshots, settings.CloseToTray).ShouldBe((false, false, false));
        changed.ShouldBeGreaterThanOrEqualTo(14);
        settings.Grids.ShouldBe(Enum.GetValues<GridOverlay>());
        settings.Sensitivities.ShouldBe(Enum.GetValues<Sensitivity>());
        settings.Version.ShouldBe("0.2.0");
    }

    [AvaloniaFact]
    public async Task The_download_folder_is_the_default_until_another_is_chosen()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var settings = shell.Settings;
        settings.DownloadFolder.ShouldBe(FolderSink.DefaultFolder);

        settings.DownloadFolder = @"D:\Camera";
        app.Preferences.Current.DownloadFolder.ShouldBe(@"D:\Camera");
        settings.DownloadFolder = FolderSink.DefaultFolder;
        app.Preferences.Current.DownloadFolder.ShouldBeNull();
        settings.OpenDownloadsCommand.Execute(null);
        app.Desktop.Folders.ShouldBe([FolderSink.DefaultFolder]);
    }

    [AvaloniaFact]
    public async Task A_setting_that_could_not_be_kept_still_holds_for_this_run()
    {
        await using var app = new TestApp(Returning);
        var blocked = Path.Combine(app.Folder, "blocked");
        File.WriteAllText(blocked, "a file where a folder should be");
        var store = new PreferencesStore(Path.Combine(blocked, "preferences.json"));
        using var shell = new ShellViewModel(app.Parts with { Preferences = store });

        shell.Settings.Flip = true;

        store.Current.Flip.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task The_cameras_own_settings_are_listed_once_connected_and_a_choice_goes_to_the_camera()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var settings = shell.Settings;
        settings.CameraConnected.ShouldBeFalse();
        settings.CameraLocked.ShouldBe("Connect a camera to change its own settings.");
        settings.CameraSettings.ShouldBeEmpty();

        await app.ConnectedAsync();
        await TestApp.EventuallyAsync(() => settings.CameraSettings.Count > 0);

        settings.CameraLocked.ShouldBeNull();
        settings.CameraFacts.ShouldNotBeEmpty();
        settings.CameraFacts.ShouldAllBe(f => !f.IsChangeable);
        var resolution = settings.CameraSettings.First(s => s.Name == "Resolution");
        resolution.Category.ShouldNotBeNullOrEmpty();
        resolution.Shown.ShouldBe(resolution.Selected!.Value.Label);
        var other = resolution.Choices.First(c => c != resolution.Selected);

        resolution.Selected = other;

        await TestApp.EventuallyAsync(() => app.Control.SettingsWritten.Contains((resolution.Setting.Menu.Id, other.Value)));
        await TestApp.EventuallyAsync(() => resolution.Shown == other.Label);
        app.Control.SettingsWritten.Count.ShouldBe(1);

        // Clearing the choice asks the camera for nothing.
        resolution.Selected = null;
        await Task.Delay(100);
        app.Control.SettingsWritten.Count.ShouldBe(1);
    }

    [AvaloniaFact]
    public async Task The_cameras_settings_are_locked_while_it_records()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        await app.ConnectedAsync();

        await app.Controller.ToggleRecording()!;

        await TestApp.EventuallyAsync(() => shell.Settings.CameraLocked == "The camera's settings are locked while it records.");
    }

    [AvaloniaFact]
    public async Task A_setting_the_camera_will_not_read_shows_no_choice()
    {
        await using var app = new TestApp(Returning);
        app.Control.ForcedRefusals[Sightline.Protocol.GpSock.GpSockCommand.MenuGetParameter] = Sightline.Protocol.GpSock.NakCode.InvalidCommand;
        using var shell = new ShellViewModel(app.Parts);

        await app.ConnectedAsync();
        await TestApp.EventuallyAsync(() => shell.Settings.CameraSettings.Count > 0);

        shell.Settings.CameraSettings.ShouldAllBe(s => s.Selected == null && s.Shown == null);
    }

    [AvaloniaFact]
    public async Task About_opens_the_source_and_the_introduction()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);

        shell.Settings.OpenSourceCommand.Execute(null);
        shell.Settings.ShowIntroductionCommand.Execute(null);

        app.Desktop.Links.ShouldBe([SettingsViewModel.SourceUrl]);
        shell.ShowOnboarding.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => new SettingsViewModel(null!, shell));
        Should.Throw<ArgumentNullException>(() => new SettingsViewModel(app.Parts, null!));
    }
}
