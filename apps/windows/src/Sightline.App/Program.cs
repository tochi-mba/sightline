using Avalonia;

namespace Sightline.App;

/// <summary>Entry point for Sightline for Windows.</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Builds the application. Public so a headless test host can start it too.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SightlineApplication>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
