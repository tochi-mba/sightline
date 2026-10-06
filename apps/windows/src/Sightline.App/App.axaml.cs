using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sightline.App.Services;
using Sightline.App.ViewModels;
using Sightline.App.Views;
using Sightline.Core;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;
using Sightline.Platform.Windows.Install;
using Sightline.Platform.Windows.Network;
using Sightline.Platform.Windows.Wlan;

namespace Sightline.App;

/// <summary>Sightline for Windows, a REX Technologies product.</summary>
public sealed class SightlineApplication : Application
{
    /// <summary>How long leaving the camera may take as the app closes: the adapter is put back in that time.</summary>
    private static readonly TimeSpan LeaveTime = TimeSpan.FromSeconds(10);

    /// <summary>This copy's claim to be the only one, which another start wakes; null in tests.</summary>
    internal static SingleInstance? Instance { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (shell, close) = Build();
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            ShowTray(desktop, window, shell);
            Instance?.OnWake(() => Dispatcher.UIThread.Post(window.Bring));
            // The installer replacing this build asks it to quit, which leaves the camera properly.
            Instance?.OnQuit(() => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
            desktop.ShutdownRequested += (_, _) => close();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The tray icon: back to the window, Sentry on or off, and quitting, which nothing else does.</summary>
    private void ShowTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, ShellViewModel shell)
    {
        var show = new NativeMenuItem("Show Sightline");
        show.Click += (_, _) => window.Bring();
        var sentry = new NativeMenuItem(shell.SentryAction) { Command = shell.ToggleSentryCommand };
        var quit = new NativeMenuItem("Quit Sightline");
        quit.Click += (_, _) => desktop.Shutdown();
        var tray = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = shell.TrayText,
            Menu = new NativeMenu { show, sentry, new NativeMenuItemSeparator(), quit },
        };
        tray.Clicked += (_, _) => window.Bring();
        shell.PropertyChanged += (_, e) =>
        {
            tray.ToolTipText = shell.TrayText;
            sentry.Header = shell.SentryAction;
        };
        TrayIcon.SetIcons(this, [tray]);
    }

    /// <summary>
    /// Builds the window from the real parts: this PC's Wi-Fi, the camera on the adapter the person chooses,
    /// Sentry, and the preferences kept for this person.
    /// </summary>
    /// <returns>The window's view model, and what to run as the app closes.</returns>
    private static (ShellViewModel Shell, Action Close) Build()
    {
        var preferences = new PreferencesStore(PreferencesStore.DefaultPath);
        var wlan = new WindowsWlanClient();
        var network = new SystemNetworkState(CameraAddress.Default);
        var choice = new CameraChoice();
        var controller = new CameraController(
            new WifiCameraLink(wlan, network, () => choice.Current),
            reconnects: () => preferences.Current.Reconnect);
        var alarms = new WindowsSentryActions(WindowsSentryActions.DefaultFolder);
        var sentry = new SentryRunner(controller, () => preferences.Current.Sentry, alarms, LumaSampler.Grid);
        var parts = new AppParts(
            controller,
            choice,
            preferences,
            new CameraFinder(wlan, network),
            sentry,
            alarms,
            new WindowsDesktop(),
            Pictures.Decode,
            action => Dispatcher.UIThread.Post(action),
            AppVersion.Of(typeof(SightlineApplication)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sightline"),
            Updates: new VelopackUpdateSource());
        var shell = new ShellViewModel(parts);

        return (shell, () =>
        {
            shell.Dispose();
            sentry.Dispose();
            // Leaving the camera puts the adapter back on the network it was taken from; the app waits for
            // that, a little, rather than closing with the PC still on the camera's Wi-Fi.
            Task.Run(async () => await controller.DisposeAsync().ConfigureAwait(false)).Wait(LeaveTime);
        });
    }
}
