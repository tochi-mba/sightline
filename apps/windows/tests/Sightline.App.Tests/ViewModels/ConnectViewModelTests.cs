using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Settings;
using Sightline.Core.Testing;

namespace Sightline.App.Tests.ViewModels;

/// <summary>Choosing a camera and the adapter to reach it through, and joining it.</summary>
public sealed class ConnectViewModelTests
{
    private const string Camera = "ActionCam_f8160c220c72";

    private static ConnectViewModel Panel(TestApp app, TimeSpan? searchTime = null)
    {
        var panel = new ConnectViewModel(app.Parts, searchTime);
        app.Controller.StateChanged += _ => panel.Apply(app.Controller.State);
        return panel;
    }

    private static void TwoAdaptersSeeTheCamera(TestApp app)
    {
        app.Wlan.AdapterList.Add(Adapters.BuiltIn(new WifiConnection("Home", "Home")));
        app.Wlan.AdapterList.Add(Adapters.Dongle());
        app.Wlan.Visible[Adapters.BuiltInId] = [new WifiNetwork(Camera, 90)];
        app.Wlan.Visible[Adapters.DongleId] = [new WifiNetwork(Camera, 60)];
    }

    [AvaloniaFact]
    public async Task Looking_offers_each_camera_once_per_adapter_and_picks_the_one_used_last()
    {
        await using var app = new TestApp(Preferences.Default with { LastCamera = Camera, LastAdapter = Adapters.BuiltInId });
        TwoAdaptersSeeTheCamera(app);
        var panel = Panel(app);

        await panel.FindCommand.ExecuteAsync(null);

        panel.Cameras.Select(c => c.AdapterId).ShouldBe([Adapters.DongleId, Adapters.BuiltInId]);
        panel.Selected!.AdapterId.ShouldBe(Adapters.BuiltInId);
        panel.Message.ShouldBe("Pick the camera, and the Wi-Fi adapter to reach it through, then connect.");
        panel.Searching.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task With_nothing_remembered_the_best_choice_is_picked_and_with_nothing_in_range_it_says_so()
    {
        await using var app = new TestApp();
        var panel = Panel(app);

        await panel.FindCommand.ExecuteAsync(null);
        panel.Cameras.ShouldBeEmpty();
        panel.Selected.ShouldBeNull();
        panel.Message.ShouldBe("No camera in range. Press its Wi-Fi button, keep it close, and look again.");

        TwoAdaptersSeeTheCamera(app);
        await panel.FindCommand.ExecuteAsync(null);
        panel.Selected!.AdapterId.ShouldBe(Adapters.DongleId);

        // The camera remembered, but not the adapter: the best way to it.
        app.Preferences.Update(p => p with { LastCamera = Camera });
        await panel.FindCommand.ExecuteAsync(null);
        panel.Selected!.AdapterId.ShouldBe(Adapters.DongleId);
    }

    [AvaloniaFact]
    public async Task A_scan_windows_refuses_or_one_that_takes_too_long_says_so()
    {
        await using var app = new TestApp();
        app.Wlan.AdapterList.Add(Adapters.Dongle());
        var panel = Panel(app, TimeSpan.FromMilliseconds(50));

        app.Wlan.Failing.Add("scan");
        await panel.FindCommand.ExecuteAsync(null);
        panel.Message.ShouldBe($"Windows could not look for Wi-Fi: failed: scan {Adapters.DongleId}");

        app.Wlan.Failing.Clear();
        app.Wlan.ScanWaits = true;
        await panel.FindCommand.ExecuteAsync(null);
        panel.Message.ShouldBe("Windows took too long to look for Wi-Fi. Try again.");
        panel.Searching.ShouldBeFalse();
        panel.FindCommand.CanExecute(null).ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task An_adapter_that_leaves_a_network_needs_consent_and_a_new_choice_asks_again()
    {
        await using var app = new TestApp();
        TwoAdaptersSeeTheCamera(app);
        var panel = Panel(app);
        await panel.FindCommand.ExecuteAsync(null);
        var builtIn = panel.Cameras.Single(c => c.AdapterId == Adapters.BuiltInId);

        panel.Selected = builtIn;
        panel.NeedsConsent.ShouldBeTrue();
        panel.Advice.ShouldBe(builtIn.Explanation);
        panel.ConnectCommand.CanExecute(null).ShouldBeFalse();
        panel.Consented = true;
        panel.ConnectCommand.CanExecute(null).ShouldBeTrue();

        panel.Selected = panel.Cameras.Single(c => c.AdapterId == Adapters.DongleId);
        panel.Consented.ShouldBeFalse();
        panel.NeedsConsent.ShouldBeFalse();
        panel.ConnectCommand.CanExecute(null).ShouldBeTrue();

        panel.Selected = null;
        panel.NeedsConsent.ShouldBeFalse();
        panel.Advice.ShouldBe("");
        panel.ConnectCommand.CanExecute(null).ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task The_password_must_be_one_a_camera_can_have()
    {
        await using var app = new TestApp();
        TwoAdaptersSeeTheCamera(app);
        var panel = Panel(app);
        await panel.FindCommand.ExecuteAsync(null);
        panel.Password.ShouldBe("12345678");
        panel.PasswordProblem.ShouldBeNull();

        panel.Password = "short";

        panel.PasswordProblem.ShouldBe("8 to 63 letters, numbers or symbols.");
        panel.ConnectCommand.CanExecute(null).ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Connecting_remembers_the_choice_joins_it_and_leaving_lets_it_go()
    {
        await using var app = new TestApp();
        TwoAdaptersSeeTheCamera(app);
        var panel = Panel(app);
        await panel.FindCommand.ExecuteAsync(null);
        panel.Password = "camera-pass";

        panel.ConnectCommand.Execute(null);

        app.Choice.Current.ShouldBe(new CameraTarget(Adapters.DongleId, Camera, "camera-pass", false));
        (app.Preferences.Current.LastCamera, app.Preferences.Current.LastAdapter, app.Preferences.Current.CameraPassword)
            .ShouldBe((Camera, Adapters.DongleId, "camera-pass"));
        await TestApp.EventuallyAsync(() => app.Controller.State.IsConnected);
        panel.Busy.ShouldBeTrue();
        panel.ConnectCommand.CanExecute(null).ShouldBeFalse();

        await panel.DisconnectCommand.ExecuteAsync(null);

        panel.Busy.ShouldBeFalse();
        panel.Problem.ShouldBeNull();
    }

    [AvaloniaFact]
    public async Task A_camera_that_could_not_be_joined_says_why_and_what_to_do()
    {
        await using var app = new TestApp();
        TwoAdaptersSeeTheCamera(app);
        var panel = Panel(app);
        await panel.FindCommand.ExecuteAsync(null);
        app.Link.Failures.Enqueue(new CameraLinkException(LinkFailure.NotJoined, "Not joined."));

        panel.ConnectCommand.Execute(null);

        await TestApp.EventuallyAsync(() => panel.Problem is not null);
        panel.Problem.ShouldBe(new Problem(ProblemKind.NotJoined, "x").Explanation);
        panel.Busy.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void It_needs_the_windows_parts() =>
        Should.Throw<ArgumentNullException>(() => new ConnectViewModel(null!));
}
