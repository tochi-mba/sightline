using System.Collections.Immutable;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Camera;
using Sightline.Core.Settings;
using Sightline.Protocol.GpSock;

namespace Sightline.App.Tests.ViewModels;

/// <summary>The camera's card: its files, copying them to this PC, and deleting them from the card.</summary>
public sealed class LibraryViewModelTests
{
    private static readonly DateTime Sunday = new(2026, 10, 4, 18, 35, 0);

    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.2.0" };

    /// <summary>A shell on the Library page, with a camera holding a photo and a video, connected.</summary>
    private static async Task<ShellViewModel> OpenCardAsync(TestApp app)
    {
        app.Control.ThumbnailOf = _ => TestPictures.Jpeg(150);
        app.Control.AddFile('J', Sunday, TestPictures.Jpeg(120));
        app.Control.AddFile('A', Sunday.AddDays(1), new byte[5000]);
        app.Control.DownloadChunk = 1000;
        var shell = new ShellViewModel(app.Parts);
        await app.ConnectedAsync();
        // The fake camera refuses thumbnails while it streams, and the Live page started the stream.
        app.Control.IsStreaming = false;
        shell.GoCommand.Execute(Page.Library);
        // Thumbnails arrive while the card is still being read; the page is ready once the camera is free.
        await TestApp.EventuallyAsync(() =>
            shell.Library.Items.Count == 2 && shell.Library.Items.All(i => i.Thumbnail is not null) && shell.Library.Idle);
        return shell;
    }

    [AvaloniaFact]
    public async Task The_card_is_listed_by_day_with_its_thumbnails()
    {
        await using var app = new TestApp(Returning);
        using var shell = await OpenCardAsync(app);
        var library = shell.Library;

        library.Heading.ShouldBe("1 video, 1 photo");
        library.Detail.ShouldBe($"Copies go to {FolderSink.DefaultFolder}.");
        library.Items.Select(i => (i.Day, i.FirstOfDay, i.Kind)).ShouldBe([
            ("Monday 5 October 2026", true, "Video"), ("Sunday 4 October 2026", true, "Photo")]);
        var photo = library.Items[1];
        photo.Name.ShouldBe(photo.File.DisplayName);
        photo.Size.ShouldEndWith(" KB");
        photo.Description.ShouldStartWith("Photo, 4 October 2026, 18:35");
        photo.Transfer.ShouldBeNull();

        // A newer thumbnail replaces the old; the same one is not decoded again.
        var first = photo.Thumbnail;
        var newer = TestPictures.Jpeg(200);
        photo.ShowThumbnail(newer, TestApp.Decode);
        photo.Thumbnail.ShouldNotBeSameAs(first);
        var second = photo.Thumbnail;
        photo.ShowThumbnail(newer, TestApp.Decode);
        photo.Thumbnail.ShouldBeSameAs(second);
    }

    [AvaloniaFact]
    public async Task The_heading_says_why_nothing_shows()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var library = shell.Library;
        var connected = CameraState.Initial with { Connection = Connection.Connected.Instance };

        library.Heading.ShouldBe("No camera connected");
        library.Apply(connected with { Library = Library.Unread with { Reading = true } });
        library.Heading.ShouldBe("Reading the card");
        library.Apply(connected);
        library.Heading.ShouldBe("The card");
        library.Apply(connected with { Library = Library.Unread with { Files = ImmutableList<CameraFile>.Empty } });
        library.Heading.ShouldBe("The card is empty");
        library.Detail.ShouldBe("The camera says its card is empty, or that it has no card.");
    }

    [AvaloniaFact]
    public async Task Picked_files_are_copied_to_the_folder_chosen_and_say_so()
    {
        await using var app = new TestApp(Returning);
        using var shell = await OpenCardAsync(app);
        var library = shell.Library;
        var folder = Path.Combine(app.Folder, "copies");
        app.Preferences.Update(p => p with { DownloadFolder = folder });
        library.CopyCommand.CanExecute(null).ShouldBeFalse();

        library.Items[1].Selected = true;
        library.SelectedCount.ShouldBe(1);
        library.CopyLabel.ShouldBe("Copy 1 to this PC");
        // Picked, but with the camera busy, nothing can be done with them yet.
        library.Apply(app.Controller.State with { Task = CameraTask.ReadingCard });
        library.CopyCommand.CanExecute(null).ShouldBeFalse();
        library.Apply(app.Controller.State);
        library.CopyCommand.CanExecute(null).ShouldBeTrue();
        library.CopyCommand.Execute(null);

        library.SelectedCount.ShouldBe(0);
        await TestApp.EventuallyAsync(() => library.Items[1].Transfer == "On this PC");
        Directory.GetFiles(folder).Single().ShouldEndWith(".jpg");
        app.Control.Files.Count.ShouldBe(2);
    }

    [AvaloniaFact]
    public async Task Copying_can_clear_the_card_and_the_folder_opens()
    {
        await using var app = new TestApp(Returning with { DeleteAfterCopy = true });
        using var shell = await OpenCardAsync(app);
        var library = shell.Library;
        app.Preferences.Update(p => p with { DownloadFolder = Path.Combine(app.Folder, "copies") });

        library.SelectAllCommand.Execute(null);
        library.SelectedCount.ShouldBe(2);
        library.CopyCommand.Execute(null);

        await TestApp.EventuallyAsync(() => app.Control.Files.Count == 0);
        await TestApp.EventuallyAsync(() => library.Items.Count == 0);
        library.OpenFolderCommand.Execute(null);
        app.Desktop.Folders.ShouldBe([Path.Combine(app.Folder, "copies")]);
    }

    [AvaloniaFact]
    public async Task Deleting_asks_first_and_can_be_called_off()
    {
        await using var app = new TestApp(Returning);
        using var shell = await OpenCardAsync(app);
        var library = shell.Library;

        library.SelectAllCommand.Execute(null);
        library.DeleteCommand.Execute(null);
        library.ConfirmingDelete.ShouldBeTrue();
        library.DeleteQuestion.ShouldBe("Delete 2 files from the camera?");
        library.CancelDeleteCommand.Execute(null);
        library.ConfirmingDelete.ShouldBeFalse();
        app.Control.Files.Count.ShouldBe(2);

        library.SelectNoneCommand.Execute(null);
        library.Items[0].Selected = true;
        library.DeleteQuestion.ShouldBe("Delete this file from the camera?");
        library.DeleteCommand.Execute(null);
        library.ConfirmDeleteCommand.Execute(null);

        await TestApp.EventuallyAsync(() => app.Control.Files.Count == 1);
        await TestApp.EventuallyAsync(() => library.Items.Count == 1);
        library.ConfirmingDelete.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task The_card_can_be_read_again_only_while_the_camera_is_free()
    {
        await using var app = new TestApp(Returning);
        using var shell = await OpenCardAsync(app);
        var library = shell.Library;
        await TestApp.EventuallyAsync(() => library.Idle);
        library.RefreshCommand.CanExecute(null).ShouldBeTrue();

        library.RefreshCommand.Execute(null);

        await TestApp.EventuallyAsync(() => library.Idle && library.Items.Count == 2);
        await app.Controller.DisconnectAsync();
        await TestApp.EventuallyAsync(() => !library.Connected);
        library.RefreshCommand.CanExecute(null).ShouldBeFalse();
        library.Heading.ShouldBe("No camera connected");
    }

    [AvaloniaFact]
    public async Task Opening_the_page_with_no_camera_or_a_card_already_read_reads_nothing()
    {
        await using var app = new TestApp(Returning);
        var library = new LibraryViewModel(app.Parts);
        library.Opened();
        app.Controller.State.Library.Reading.ShouldBeFalse();

        using var shell = await OpenCardAsync(app);
        shell.Library.Opened();
        app.Controller.State.Library.Reading.ShouldBeFalse();

        library.Dispose();
        Should.Throw<ArgumentNullException>(() => new LibraryViewModel(null!));
    }
}
