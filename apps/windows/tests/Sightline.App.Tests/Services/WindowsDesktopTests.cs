using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.Services;
using Sightline.App.Tests.ViewModels;
using Xunit;

namespace Sightline.App.Tests.Services;

/// <summary>File Explorer, the browser, pictures and the version.</summary>
public sealed class WindowsDesktopTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "sightline-desktop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_folder_opens_in_explorer_made_first_if_missing_and_a_link_in_the_browser()
    {
        var started = new List<ProcessStartInfo>();
        var desktop = new WindowsDesktop(started.Add);

        desktop.OpenFolder(folder);
        desktop.OpenLink("https://github.com/tochi-mba/sightline");

        Directory.Exists(folder).ShouldBeTrue();
        started[0].FileName.ShouldBe("explorer.exe");
        started[0].ArgumentList.ShouldBe([folder]);
        started[0].UseShellExecute.ShouldBeFalse();
        started[1].FileName.ShouldBe("https://github.com/tochi-mba/sightline");
        started[1].UseShellExecute.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => desktop.OpenFolder(" "));
        Should.Throw<ArgumentException>(() => desktop.OpenLink(""));
        new WindowsDesktop().ShouldNotBeNull();
    }

    [AvaloniaFact]
    public void A_picture_decodes_and_anything_else_gives_none()
    {
        using var picture = Pictures.Decode(TestPictures.Jpeg(100, 64, 36));
        picture!.PixelSize.Width.ShouldBe(64);

        Pictures.Decode([0xFF, 0xD8, 0xFF, 0xD9]).ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => Pictures.Decode(null!));
    }

    [Fact]
    public void The_version_is_the_build_without_its_source_revision()
    {
        var version = AppVersion.Of(typeof(SightlineApplication));

        version.ShouldNotContain("+");
        version.ShouldBe(File.ReadAllText(Path.Combine(RepositoryRoot(), "VERSION")).Trim());
        AppVersion.Of(typeof(string)).ShouldNotContain("+");
        Should.Throw<ArgumentNullException>(() => AppVersion.Of(null!));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "VERSION")))
        {
            directory = directory.Parent;
        }

        return directory.FullName;
    }
}
