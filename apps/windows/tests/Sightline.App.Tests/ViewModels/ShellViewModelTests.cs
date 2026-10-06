using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Camera;
using Sightline.Core.Settings;
using Sightline.Core.Testing;
using Sightline.Core.Updates;

namespace Sightline.App.Tests.ViewModels;

/// <summary>The window as a whole: its pages, the camera's state, notices, and what shows on top.</summary>
public sealed class ShellViewModelTests
{
    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.1.0" };

    [AvaloniaFact]
    public async Task A_first_run_shows_the_introduction_and_finishing_it_records_the_version()
    {
        await using var app = new TestApp();
        using var shell = new ShellViewModel(app.Parts);

        shell.ShowOnboarding.ShouldBeTrue();
        shell.ShowsWhatsNew.ShouldBeFalse();
        shell.Version.ShouldBe("0.2.0");

        shell.FinishOnboardingCommand.Execute(null);

        shell.ShowOnboarding.ShouldBeFalse();
        app.Preferences.Current.OnboardingDone.ShouldBeTrue();
        app.Preferences.Current.LastVersion.ShouldBe("0.2.0");
    }

    [AvaloniaFact]
    public async Task An_update_that_earned_a_word_says_what_changed_once()
    {
        await using var app = new TestApp(Returning, [new WhatsNewEntry("0.2.0", ["Sentry on Windows."])]);
        using var shell = new ShellViewModel(app.Parts);

        shell.ShowOnboarding.ShouldBeFalse();
        shell.ShowsWhatsNew.ShouldBeTrue();
        shell.WhatsNew.Single().Lines.ShouldBe(["Sentry on Windows."]);
        app.Preferences.Current.LastVersion.ShouldBe("0.1.0");

        shell.CloseWhatsNewCommand.Execute(null);

        shell.ShowsWhatsNew.ShouldBeFalse();
        app.Preferences.Current.LastVersion.ShouldBe("0.2.0");
    }

    [AvaloniaFact]
    public async Task An_update_with_nothing_to_say_is_recorded_at_once()
    {
        await using var app = new TestApp(Returning, []);
        using var shell = new ShellViewModel(app.Parts);

        shell.ShowsWhatsNew.ShouldBeFalse();
        app.Preferences.Current.LastVersion.ShouldBe("0.2.0");
    }

    [AvaloniaFact]
    public async Task Opening_joins_the_last_camera_without_consenting_for_anybody_and_shows_its_picture()
    {
        var adapter = Adapters.DongleId;
        await using var app = new TestApp(Returning with { LastAdapter = adapter, LastCamera = "ActionCam_1", CameraPassword = "camera-pass" });
        using var shell = new ShellViewModel(app.Parts);
        shell.ShowsConnect.ShouldBeTrue();
        shell.ConnectionWord.ShouldBeNull();

        shell.Start();

        app.Choice.Current.ShouldBe(new CameraTarget(adapter, "ActionCam_1", "camera-pass", Consented: false));
        await TestApp.EventuallyAsync(() => shell.Camera.IsConnected);
        shell.ConnectionWord.ShouldBe("Connected");
        shell.ShowsConnect.ShouldBeFalse();
        // The Live page shows first, and holds the picture open while it does.
        await TestApp.EventuallyAsync(() => shell.Live.Picture is not null);
    }

    [AvaloniaFact]
    public async Task Opening_joins_nothing_when_told_not_to_or_with_no_camera_remembered()
    {
        await using var off = new TestApp(Returning with { AutoConnect = false, LastAdapter = Adapters.DongleId, LastCamera = "ActionCam_1" });
        using (var shell = new ShellViewModel(off.Parts))
        {
            shell.Start();
        }

        await using var fresh = new TestApp(Returning);
        using (var shell = new ShellViewModel(fresh.Parts))
        {
            shell.Start();
        }

        await using var noCamera = new TestApp(Returning with { LastAdapter = Adapters.DongleId });
        using (var shell = new ShellViewModel(noCamera.Parts))
        {
            shell.Start();
        }

        (off.Link.Requests.Count + fresh.Link.Requests.Count + noCamera.Link.Requests.Count).ShouldBe(0);
        off.Choice.Current.ShouldBeNull();
    }

    [AvaloniaFact]
    public async Task Each_page_shows_alone_and_only_the_live_page_holds_the_picture_open()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        app.Control.AddFile('J', new DateTime(2026, 10, 4, 18, 35, 0), TestPictures.Jpeg(120));
        await app.ConnectedAsync();
        await TestApp.EventuallyAsync(() => app.Controller.State.Live is LiveView.Playing);

        shell.GoCommand.Execute(Page.Library);

        (shell.IsLive, shell.IsLibrary, shell.IsSentry, shell.IsSettings).ShouldBe((false, true, false, false));
        await TestApp.EventuallyAsync(() => shell.Library.Items.Count == 1);
        await TestApp.EventuallyAsync(() => app.Controller.State.Live is LiveView.Off);

        shell.GoCommand.Execute(Page.Sentry);
        shell.IsSentry.ShouldBeTrue();
        shell.GoCommand.Execute(Page.Settings);
        shell.IsSettings.ShouldBeTrue();
        shell.GoCommand.Execute(Page.Live);
        shell.IsLive.ShouldBeTrue();
        await TestApp.EventuallyAsync(() => app.Controller.State.Live is LiveView.Playing);
    }

    [AvaloniaFact]
    public async Task A_notice_shows_until_dismissed_and_the_introduction_can_be_seen_again()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);

        app.Controller.TakePhoto().ShouldBeNull();
        await TestApp.EventuallyAsync(() => shell.Notice is not null);
        shell.Notice.ShouldBe("Connect to the camera first.");

        shell.DismissNoticeCommand.Execute(null);
        await TestApp.EventuallyAsync(() => shell.Notice is null);

        shell.ShowIntroduction();
        shell.ShowOnboarding.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Once_closed_it_follows_the_camera_no_more()
    {
        await using var app = new TestApp(Returning);
        var shell = new ShellViewModel(app.Parts);
        shell.Dispose();

        _ = app.Controller.TakePhoto();
        await Task.Delay(100);

        shell.Notice.ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => new ShellViewModel(null!));
    }
}
