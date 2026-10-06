using Avalonia;
using Sightline.App.Services;

namespace Sightline.App;

/// <summary>Entry point for Sightline for Windows.</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new SingleInstance("REXTechnologies.Sightline");
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
