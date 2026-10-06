using Avalonia;
using Sightline.Platform.Windows.Install;
using Velopack;

namespace Sightline.App;

/// <summary>Entry point for Sightline for Windows.</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // First, before anything else: when the installer runs this build to install, update or remove it,
        // the hook does its one job and the process ends here. Wiring only; the jobs are InstallLifecycle's.
        var lifecycle = InstallLifecycle.ForThisInstall();
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => lifecycle.AfterInstall())
            .OnAfterUpdateFastCallback(_ => lifecycle.AfterUpdate())
            .OnBeforeUninstallFastCallback(_ => lifecycle.BeforeUninstall())
            .Run();

        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            // Already running, perhaps only in the tray: show that one instead of starting a second.
            instance.WakeFirst();
            return;
        }

        SightlineApplication.Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Builds the application. Public so a headless test host can start it too.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SightlineApplication>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
