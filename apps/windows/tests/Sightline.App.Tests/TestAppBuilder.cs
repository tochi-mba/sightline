using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Sightline.App.Tests.TestAppBuilder))]

namespace Sightline.App.Tests;

/// <summary>Boots the real Sightline application on the headless platform.</summary>
/// <remarks>
/// Real Skia rendering rather than the headless drawing stub, so a JPEG decodes into a picture exactly as
/// it does in the app, and the views are drawn with the REX theme they ship with.
/// </remarks>
public static class TestAppBuilder
{
    /// <summary>Builds the headless application.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SightlineApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
