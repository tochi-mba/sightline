using Shouldly;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;
using Xunit;

namespace Sightline.Core.Tests.Settings;

/// <summary>The Windows app's settings, and the file they are kept in.</summary>
public sealed class PreferencesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "sightline-preferences-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(folder, "preferences.json");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_defaults_are_the_android_apps()
    {
        var defaults = Preferences.Default;

        defaults.AutoConnect.ShouldBeTrue();
        defaults.Reconnect.ShouldBeTrue();
        defaults.CameraPassword.ShouldBe("12345678");
        defaults.Fit.ShouldBe(PictureFit.Fit);
        defaults.Grid.ShouldBe(GridOverlay.None);
        defaults.SentrySensitivity.ShouldBe(Sensitivity.Medium);
        defaults.SentryArmDelaySeconds.ShouldBe(10);
        defaults.SentryCooldownSeconds.ShouldBe(30);
        defaults.SentryRecords.ShouldBeTrue();
        defaults.SentrySnapshots.ShouldBeTrue();
        defaults.CheckForUpdates.ShouldBeTrue();
        defaults.CloseToTray.ShouldBeTrue();
        defaults.OnboardingDone.ShouldBeFalse();
        (defaults.LastAdapter, defaults.LastCamera, defaults.DownloadFolder, defaults.LastVersion).ShouldBe((null, null, null, null));
        (defaults.Flip, defaults.Mirror, defaults.ShowFrameRate, defaults.DeleteAfterCopy).ShouldBe((false, false, false, false));
    }

    [Fact]
    public void Sentry_runs_as_the_preferences_say()
    {
        var sentry = (Preferences.Default with
        {
            SentrySensitivity = Sensitivity.High,
            SentryArmDelaySeconds = 0,
            SentryCooldownSeconds = 60,
            SentryRecords = false,
            SentrySnapshots = false,
        }).Sentry;

        sentry.Watch.Sensitivity.ShouldBe(Sensitivity.High);
        sentry.Watch.ArmDelay.ShouldBe(TimeSpan.Zero);
        sentry.Watch.Cooldown.ShouldBe(TimeSpan.FromMinutes(1));
        sentry.Records.ShouldBeFalse();
        sentry.SaveSnapshots.ShouldBeFalse();
    }

    [Fact]
    public void Anything_out_of_range_goes_back_to_its_default()
    {
        var broken = Preferences.Default with
        {
            CameraPassword = "short",
            LastCamera = " ",
            Fit = (PictureFit)9,
            Grid = (GridOverlay)9,
            DownloadFolder = "",
            SentrySensitivity = (Sensitivity)9,
            SentryArmDelaySeconds = 7,
            SentryCooldownSeconds = 5,
            LastVersion = "",
        };

        broken.Validated().ShouldBe(Preferences.Default);
        var kept = Preferences.Default with
        {
            SentryArmDelaySeconds = 120,
            SentryCooldownSeconds = 600,
            LastCamera = "ActionCam_1",
            LastVersion = "0.1.0",
            DownloadFolder = @"D:\Camera",
        };
        kept.Validated().ShouldBe(kept);
    }

    [Fact]
    public void A_camera_password_is_eight_to_sixty_three_plain_characters()
    {
        Preferences.IsValidPassword("x".PadLeft(8, 'x')).ShouldBeTrue();
        Preferences.IsValidPassword(new string('x', 63)).ShouldBeTrue();
        Preferences.IsValidPassword(new string('x', 64)).ShouldBeFalse();
        Preferences.IsValidPassword("tab\tthere!").ShouldBeFalse();
        Preferences.IsValidPassword("café-password").ShouldBeFalse();
        Preferences.IsValidPassword(null).ShouldBeFalse();
    }

    [Fact]
    public void Changes_are_saved_and_read_back()
    {
        var store = new PreferencesStore(File);
        var seen = new List<Preferences>();
        store.Changed += seen.Add;

        store.Update(p => p with { Grid = GridOverlay.Thirds, LastAdapter = Guid.Empty, LastCamera = "ActionCam_f8160c220c72" });
        store.Update(p => p with { Grid = GridOverlay.Thirds });

        seen.Count.ShouldBe(1);
        store.Path.ShouldBe(File);
        var reopened = new PreferencesStore(File);
        reopened.Current.ShouldBe(store.Current);
        reopened.Current.Grid.ShouldBe(GridOverlay.Thirds);
        // Changed with nobody listening, as a store is before the window opens.
        reopened.Update(p => p with { Flip = true });
        new PreferencesStore(File).Current.Flip.ShouldBeTrue();
        System.IO.File.ReadAllText(File).ShouldContain("\"Grid\": \"Thirds\"");
        System.IO.File.Exists(File + ".new").ShouldBeFalse();
    }

    [Fact]
    public void A_missing_unreadable_or_empty_file_gives_the_defaults()
    {
        new PreferencesStore(File).Current.ShouldBe(Preferences.Default);

        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(File, "{ not json");
        new PreferencesStore(File).Current.ShouldBe(Preferences.Default);

        System.IO.File.WriteAllText(File, "null");
        new PreferencesStore(File).Current.ShouldBe(Preferences.Default);

        System.IO.File.WriteAllText(File, "{ \"CameraPassword\": \"short\", \"SentryArmDelaySeconds\": 999 }");
        new PreferencesStore(File).Current.ShouldBe(Preferences.Default);
    }

    [Fact]
    public void It_needs_somewhere_to_keep_them()
    {
        Should.Throw<ArgumentException>(() => new PreferencesStore(" "));
        Should.Throw<ArgumentNullException>(() => new PreferencesStore(File).Update(null!));
    }

    [Fact]
    public void Settings_are_kept_outside_the_folder_uninstalling_deletes()
    {
        // Found on 2026-10-06: the installer's folder, %LOCALAPPDATA%\Sightline, was also the settings folder,
        // so uninstalling deleted the person's settings without asking.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installFolder = Path.Combine(local, "Sightline");

        PreferencesStore.DefaultPath.ShouldBe(Path.Combine(local, "REX Technologies", "Sightline", "preferences.json"));
        PreferencesStore.DefaultPath.ShouldNotStartWith(installFolder + Path.DirectorySeparatorChar);
    }
}
