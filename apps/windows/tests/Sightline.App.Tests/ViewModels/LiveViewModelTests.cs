using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Camera;
using Sightline.Core.Settings;
using Sightline.Testing;

namespace Sightline.App.Tests.ViewModels;

/// <summary>The live picture, the shutter, the mode, and snapshots.</summary>
public sealed class LiveViewModelTests
{
    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.2.0" };

    [AvaloniaFact]
    public async Task Pictures_are_shown_as_they_come_and_an_unreadable_one_keeps_the_last()
    {
        await using var app = new TestApp(Returning);
        app.Link.Stream = s =>
        {
            s.Frames.Add(TestPictures.Jpeg(90));
            s.Frames.AddRange(Enumerable.Range(0, 20).Select(_ => FakeRtspCamera.Jpeg(600)));
        };
        using var shell = new ShellViewModel(app.Parts);
        var live = shell.Live;
        live.Message.ShouldBeNull();

        await app.ConnectedAsync();
        app.Controller.ShowLivePicture();

        await TestApp.EventuallyAsync(() => live.Picture is not null);
        var first = live.Picture;
        await TestApp.EventuallyAsync(() => app.Link.Streams.Count > 0 && app.Link.Streams[0].Verbs.Contains("PLAY"));
        await Task.Delay(200);
        live.Picture.ShouldBeSameAs(first);
        live.Ready.ShouldBeTrue();
        live.Status.ShouldBe("Room for 12:44:21 more video  ·  On USB power");
        live.ShutterLabel.ShouldBe("Start recording");
        live.FrameRate.ShouldBe("");
    }

    [AvaloniaFact]
    public async Task The_picture_goes_once_the_camera_does()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        await app.ConnectedAsync();
        app.Controller.ShowLivePicture();
        await TestApp.EventuallyAsync(() => shell.Live.Picture is not null);

        await app.Controller.DisconnectAsync();

        await TestApp.EventuallyAsync(() => shell.Live.Picture is null);
        shell.Live.Ready.ShouldBeFalse();
        shell.Live.SaveSnapshotCommand.Execute(null);
        shell.Live.Snapshot.ShouldBe("No picture yet to save.");
    }

    [AvaloniaFact]
    public async Task The_frame_rate_shows_only_when_asked_for()
    {
        await using var app = new TestApp(Returning with { ShowFrameRate = true });
        app.Link.Stream = s =>
        {
            s.Frames.AddRange(Enumerable.Range(0, 60).Select(i => TestPictures.Jpeg((byte)(80 + i))));
            s.Pace = TimeSpan.FromMilliseconds(30);
        };
        using var shell = new ShellViewModel(app.Parts);

        await app.ConnectedAsync();
        app.Controller.ShowLivePicture();

        await TestApp.EventuallyAsync(() => shell.Live.FrameRate.EndsWith(" fps", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_shutter_records_and_the_mode_switches()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var live = shell.Live;
        await app.ConnectedAsync();
        await TestApp.EventuallyAsync(() => live.Ready);

        live.ShutterCommand.Execute(null);
        await TestApp.EventuallyAsync(() => live.Recording);
        live.ShutterLabel.ShouldBe("Stop recording");
        await TestApp.EventuallyAsync(() => live.Ready);
        live.ShutterCommand.Execute(null);
        await TestApp.EventuallyAsync(() => !live.Recording && live.Ready);

        live.UsePhotosCommand.Execute(null);
        await TestApp.EventuallyAsync(() => live.Photos && live.Ready);
        live.ShutterLabel.ShouldBe("Take a photo");
        live.UseVideoCommand.Execute(null);
        await TestApp.EventuallyAsync(() => !live.Photos);
    }

    [AvaloniaFact]
    public async Task A_snapshot_saves_the_picture_as_the_camera_sent_it_or_says_why_not()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var live = shell.Live;
        await app.ConnectedAsync();
        app.Controller.ShowLivePicture();
        await TestApp.EventuallyAsync(() => live.Picture is not null);

        live.SaveSnapshotCommand.Execute(null);
        live.SaveSnapshotCommand.Execute(null);

        var saved = Directory.GetFiles(app.Parts.SnapshotFolder).Order().ToList();
        saved.Count.ShouldBe(2);
        File.ReadAllBytes(saved[0]).ShouldBe(TestPictures.Jpeg(90));
        live.Snapshot!.ShouldStartWith("Saved sightline-");

        Directory.Delete(app.Parts.SnapshotFolder, recursive: true);
        File.WriteAllText(app.Parts.SnapshotFolder, "a file where the folder should be");
        live.SaveSnapshotCommand.Execute(null);
        live.Snapshot.ShouldStartWith("The snapshot was not saved: ");
    }

    [AvaloniaFact]
    public async Task How_the_picture_is_shown_follows_the_settings()
    {
        await using var app = new TestApp(Returning);
        using var shell = new ShellViewModel(app.Parts);
        var live = shell.Live;
        var changed = new List<string?>();
        ((INotifyPropertyChanged)live).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        (live.Fill, live.Flip, live.Mirror, live.Grid).ShouldBe((false, false, false, GridOverlay.None));
        (live.PictureStretch, live.PictureAngle, live.PictureScaleX, live.ShowsThirds, live.ShowsCentre)
            .ShouldBe((Stretch.Uniform, 0d, 1d, false, false));

        app.Preferences.Update(p => p with { Fit = PictureFit.Fill, Flip = true, Mirror = true, Grid = GridOverlay.Thirds });

        (live.Fill, live.Flip, live.Mirror, live.Grid).ShouldBe((true, true, true, GridOverlay.Thirds));
        (live.PictureStretch, live.PictureAngle, live.PictureScaleX, live.ShowsThirds, live.ShowsCentre)
            .ShouldBe((Stretch.UniformToFill, 180d, -1d, true, false));
        changed.ShouldContain(nameof(LiveViewModel.Fill));
        changed.ShouldContain(nameof(LiveViewModel.Grid));
        changed.ShouldContain(nameof(LiveViewModel.ShowsCentre));

        app.Preferences.Update(p => p with { Grid = GridOverlay.Centre });
        (live.ShowsThirds, live.ShowsCentre).ShouldBe((false, true));
    }

    [AvaloniaFact]
    public async Task Pictures_that_arrive_while_the_last_waits_to_be_shown_are_skipped_not_queued()
    {
        await using var app = new TestApp(Returning);
        app.Link.Stream = s =>
        {
            s.Frames.AddRange(Enumerable.Range(0, 20).Select(i => TestPictures.Jpeg((byte)(80 + i))));
            s.Pace = TimeSpan.FromMilliseconds(10);
        };
        // A window thread that is busy: nothing posted runs until the test says.
        var waiting = new Queue<Action>();
        var live = new LiveViewModel(app.Parts with { Post = action => { lock (waiting) { waiting.Enqueue(action); } } });
        var pictures = 0;
        app.Controller.FrameArrived += _ => Interlocked.Increment(ref pictures);
        live.Shown(true);

        await app.ConnectedAsync();
        app.Controller.ShowLivePicture();
        await TestApp.EventuallyAsync(() => Volatile.Read(ref pictures) >= 5);

        Action[] posted;
        lock (waiting)
        {
            posted = waiting.ToArray();
        }

        posted.Length.ShouldBe(1);
        posted[0]();
        live.Picture.ShouldNotBeNull();
        live.Dispose();
    }

    [AvaloniaFact]
    public async Task Hidden_or_closed_it_lets_the_picture_go()
    {
        await using var app = new TestApp(Returning);
        var live = new LiveViewModel(app.Parts);
        app.Controller.StateChanged += _ => live.Apply(app.Controller.State);
        live.Shown(true);
        await app.ConnectedAsync();
        await TestApp.EventuallyAsync(() => live.Offered);
        live.Message.ShouldBe(CameraWords.OfferedPicture);
        live.ShowPictureCommand.Execute(null);
        await TestApp.EventuallyAsync(() => app.Controller.State.Live is LiveView.Playing);
        live.Offered.ShouldBeFalse();

        live.Shown(false);
        await TestApp.EventuallyAsync(() => app.Controller.State.Live is LiveView.Off);
        live.Shown(true);
        await TestApp.EventuallyAsync(() => live.Picture is not null);

        live.Dispose();

        live.Picture.ShouldBeNull();
        await TestApp.EventuallyAsync(() => app.Controller.State.Live is LiveView.Off);
        Should.Throw<ArgumentNullException>(() => new LiveViewModel(null!));
    }
}
