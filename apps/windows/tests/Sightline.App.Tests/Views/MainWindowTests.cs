using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Sightline.App.Tests.ViewModels;
using Sightline.App.ViewModels;
using Sightline.App.Views;
using Sightline.Core.Camera;
using Sightline.Core.Settings;

namespace Sightline.App.Tests.Views;

/// <summary>The window, drawn headless with the REX theme, driven as a person would drive it.</summary>
public sealed class MainWindowTests
{
    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.2.0" };

    private static TestApp App(Preferences? preferences = null) =>
        new(preferences, post: action => Dispatcher.UIThread.Post(action));

    private static MainWindow Show(ShellViewModel shell)
    {
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Waits for <paramref name="condition"/>, letting the window catch up meanwhile.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The window never showed it.");
            }

            await Task.Delay(20);
        }
    }

    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static bool Shown(Control control) => control.IsEffectivelyVisible;

    /// <summary>Presses <paramref name="button"/> as a mouse does: at its centre, through whatever is on top of it.</summary>
    private static void Click(Button button)
    {
        var window = (Window)TopLevel.GetTopLevel(button)!;
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_first_run_shows_the_introduction_until_it_is_finished()
    {
        await using var app = App();
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);

        Shown(Find<Border>(window, "Onboarding")).ShouldBeTrue();
        Click(Find<Border>(window, "Onboarding").GetVisualDescendants().OfType<Button>().Single());
        Dispatcher.UIThread.RunJobs();

        Shown(Find<Border>(window, "Onboarding")).ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_rail_moves_between_the_pages()
    {
        await using var app = App(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);
        Shown(Find<Button>(window, "Connect")).ShouldBeTrue();

        Click(Find<Button>(window, "LibraryTab"));
        Dispatcher.UIThread.RunJobs();
        Shown(Find<TextBlock>(window, "Heading")).ShouldBeTrue();
        Find<TextBlock>(window, "Heading").Text.ShouldBe("No camera connected");
        Shown(Find<Button>(window, "Connect")).ShouldBeFalse();

        Click(Find<Button>(window, "SentryTab"));
        Dispatcher.UIThread.RunJobs();
        Find<TextBlock>(window, "State").Text.ShouldBe("Not armed");

        Click(Find<Button>(window, "SettingsTab"));
        Dispatcher.UIThread.RunJobs();
        Shown(Find<TextBlock>(window, "CameraLocked")).ShouldBeTrue();

        Click(Find<Button>(window, "LiveTab"));
        Dispatcher.UIThread.RunJobs();
        Shown(Find<Button>(window, "Connect")).ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Connected_the_picture_shows_once_asked_with_the_shutter_and_settings_list_the_cameras_own()
    {
        await using var app = App(Returning with { Grid = GridOverlay.Thirds });
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);

        _ = app.Controller.Connect();
        await UntilAsync(() => Shown(Find<Button>(window, "ShowPicture")));
        Click(Find<Button>(window, "ShowPicture"));
        await UntilAsync(() => Find<Image>(window, "Picture").Source is not null);

        Shown(Find<Image>(window, "Picture")).ShouldBeTrue();
        Shown(Find<Grid>(window, "Thirds")).ShouldBeTrue();
        Shown(Find<Panel>(window, "Centre")).ShouldBeFalse();
        Find<Button>(window, "Shutter").Content.ShouldBe("Start recording");
        Find<TextBlock>(window, "ConnectionWord").Text.ShouldBe("Connected");

        Click(Find<Button>(window, "SettingsTab"));
        await UntilAsync(() => Find<ItemsControl>(window, "CameraSettings").ItemCount > 0);
        Find<ItemsControl>(window, "CameraSettings").GetVisualDescendants().OfType<ComboBox>().ShouldNotBeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_card_lists_its_files_and_deleting_asks_first()
    {
        await using var app = App(Returning);
        app.Control.ThumbnailOf = _ => TestPictures.Jpeg(150);
        app.Control.AddFile('J', new DateTime(2026, 10, 4, 18, 35, 0), TestPictures.Jpeg(120));
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);
        await app.ConnectedAsync();
        await UntilAsync(() => shell.Live.Offered);
        Find<TextBlock>(window, "PictureMessage").Text.ShouldBe(CameraWords.OfferedPicture);
        Click(Find<Button>(window, "ShowPicture"));
        await UntilAsync(() => app.Controller.State.HoldsLivePicture);

        Click(Find<Button>(window, "LibraryTab"));
        Click(Find<Button>(window, "ReadCard"));
        await UntilAsync(() => Find<ItemsControl>(window, "Files").ItemCount == 1 && shell.Library.Idle);

        Find<TextBlock>(window, "Heading").Text.ShouldBe("1 photo");
        Find<ItemsControl>(window, "Files").GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Click(Find<Button>(window, "Delete"));
        Dispatcher.UIThread.RunJobs();
        Shown(Find<Border>(window, "ConfirmDelete")).ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_notice_shows_in_its_bar_and_what_is_new_shows_on_top()
    {
        await using var app = new TestApp(
            Returning with { LastVersion = "0.1.0" },
            [new Sightline.Core.Updates.WhatsNewEntry("0.2.0", ["Sentry on Windows."])],
            action => Dispatcher.UIThread.Post(action));
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);
        Shown(Find<Border>(window, "WhatsNew")).ShouldBeTrue();

        _ = app.Controller.TakePhoto();
        await UntilAsync(() => Shown(Find<Border>(window, "NoticeBar")));

        // What's new covers the window until it is closed; nothing under it can be pressed.
        Click(Find<Border>(window, "NoticeBar").GetVisualDescendants().OfType<Button>().Single());
        Shown(Find<Border>(window, "NoticeBar")).ShouldBeTrue();
        Click(Find<Border>(window, "WhatsNew").GetVisualDescendants().OfType<Button>().Single());
        Shown(Find<Border>(window, "WhatsNew")).ShouldBeFalse();

        Click(Find<Border>(window, "NoticeBar").GetVisualDescendants().OfType<Button>().Single());
        await UntilAsync(() => !Shown(Find<Border>(window, "NoticeBar")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_alarm_shows_on_the_sentry_page_as_a_card_with_its_time()
    {
        await using var app = App(Returning with { SentryArmDelaySeconds = 0, SentryRecords = false });
        // A block jumping from side to side, picture after picture.
        app.Link.Stream = s =>
        {
            s.Frames.AddRange(Enumerable.Range(0, 200).Select(i => TestPictures.Moving(left: i % 2 == 0)));
            s.Pace = TimeSpan.FromMilliseconds(40);
        };
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);
        await app.ConnectedAsync();
        Click(Find<Button>(window, "SentryTab"));

        Click(Find<Button>(window, "Arm"));
        await UntilAsync(() => Find<ItemsControl>(window, "Alarms").ItemCount > 0);

        Find<ItemsControl>(window, "Alarms").GetVisualDescendants().OfType<TextBlock>()
            .ShouldContain(text => text.Text != null && text.Text.StartsWith("Movement at ", StringComparison.Ordinal));
        Click(Find<Button>(window, "Disarm"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_window_with_no_app_behind_it_simply_closes()
    {
        var window = new MainWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        closed.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Closed_by_something_other_than_the_person_it_closes_even_with_a_camera_connected()
    {
        // Only a person closing the window sends it to the tray; anything else closing it means it.
        await using var app = App(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var owner = new Window();
        owner.Show();
        var window = new MainWindow { DataContext = shell };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show(owner);
        await app.ConnectedAsync();
        await UntilAsync(() => shell.KeepsRunningWhenClosed);

        owner.Close();
        Dispatcher.UIThread.RunJobs();

        closed.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Closed_with_a_camera_connected_it_goes_to_the_tray_and_comes_back()
    {
        await using var app = App(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var window = Show(shell);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        await app.ConnectedAsync();
        await UntilAsync(() => shell.KeepsRunningWhenClosed);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        closed.ShouldBeFalse();
        window.IsVisible.ShouldBeFalse();
        window.Bring();
        window.IsVisible.ShouldBeTrue();
        window.WindowState = WindowState.Minimized;
        window.Bring();
        window.WindowState.ShouldBe(WindowState.Normal);

        await app.Controller.DisconnectAsync();
        await UntilAsync(() => !shell.KeepsRunningWhenClosed);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        closed.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Opening_the_window_starts_the_app_and_looks_for_cameras()
    {
        await using var app = App(Returning);
        app.Wlan.AdapterList.Add(Sightline.Core.Testing.Adapters.Dongle());
        using var shell = new ShellViewModel(app.Parts);

        var window = Show(shell);
        await UntilAsync(() => app.Wlan.Calls.Count > 0);

        MainWindow.Begin(null);
        window.Close();
    }
}
