using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;

namespace Sightline.App.Tests.ViewModels;

/// <summary>The security camera's page.</summary>
public sealed class SentryViewModelTests
{
    private static readonly Preferences Quick = Preferences.Default with
    {
        OnboardingDone = true,
        LastVersion = "0.2.0",
        SentryArmDelaySeconds = 0,
        SentryRecords = false,
    };

    private static SentryViewModel Page(TestApp app)
    {
        var page = new SentryViewModel(app.Parts, TimeZoneInfo.Utc);
        app.Controller.StateChanged += _ => page.Apply(app.Controller.State);
        return page;
    }

    [AvaloniaFact]
    public async Task Disarmed_it_says_how_it_would_run_and_follows_the_settings()
    {
        await using var app = new TestApp(Quick);
        using var page = Page(app);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)page).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.State.ShouldBe("Not armed");
        page.Summary.ShouldBe("Sensitivity medium, starts after 0 seconds, and does not record when it sees movement. Change these in Settings.");

        app.Preferences.Update(p => p with { SentryRecords = true, SentrySensitivity = Sensitivity.High });

        changed.ShouldContain(nameof(SentryViewModel.Summary));
        page.Summary.ShouldStartWith("Sensitivity high, starts after 0 seconds, and records on the camera");
    }

    [AvaloniaFact]
    public async Task Armed_without_a_camera_it_waits_for_one_and_disarming_stands_it_down()
    {
        await using var app = new TestApp(Quick);
        using var page = Page(app);
        page.DisarmCommand.CanExecute(null).ShouldBeFalse();

        page.ArmCommand.Execute(null);

        page.Armed.ShouldBeTrue();
        page.State.ShouldBe("Waiting for the camera");
        page.ArmCommand.CanExecute(null).ShouldBeFalse();
        page.DisarmCommand.Execute(null);
        page.Armed.ShouldBeFalse();
        page.State.ShouldBe("Not armed");
    }

    [AvaloniaFact]
    public void Each_part_of_the_watch_has_its_words()
    {
        SentryStatus Status(SentryState watch) => new(true, watch, 0, []);

        SentryViewModel.Words(SentryStatus.Off, cameraConnected: true).ShouldBe("Not armed");
        SentryViewModel.Words(SentryStatus.Off, cameraConnected: false).ShouldBe("Not armed");
        SentryViewModel.Words(Status(SentryState.Disarmed), cameraConnected: true).ShouldBe("Watching");
        SentryViewModel.Words(Status(SentryState.Watching), cameraConnected: false).ShouldBe("Waiting for the camera");
        SentryViewModel.Words(Status(SentryState.Arming), cameraConnected: true).ShouldBe("Arming");
        SentryViewModel.Words(Status(SentryState.Alarm), cameraConnected: true).ShouldBe("Movement");
        SentryViewModel.Words(Status(SentryState.Cooldown), cameraConnected: true).ShouldBe("Settling");
        SentryViewModel.Words(Status(SentryState.Watching), cameraConnected: true).ShouldBe("Watching");
    }

    [AvaloniaFact]
    public async Task Movement_raises_an_alarm_with_its_picture_and_says_where_it_was_saved()
    {
        await using var app = new TestApp(Quick);
        // A block jumping from side to side, picture after picture.
        app.Link.Stream = s =>
        {
            s.Frames.AddRange(Enumerable.Range(0, 200).Select(i => TestPictures.Moving(left: i % 2 == 0)));
            s.Pace = TimeSpan.FromMilliseconds(40);
        };
        using var page = Page(app);
        await app.ConnectedAsync();

        page.ArmCommand.Execute(null);

        await TestApp.EventuallyAsync(() => page.Alarms.Count > 0 && page.LastAlarm is not null);
        page.Alarms[0].Number.ShouldBe(1);
        page.Alarms[0].Picture.ShouldNotBeNull();
        page.LastAlarm.ShouldEndWith($"The picture is in {app.Alarms.Folder}.");
        page.Movement.ShouldBeGreaterThanOrEqualTo(0);
        page.OpenPicturesCommand.Execute(null);
        app.Desktop.Folders.ShouldBe([app.Alarms.Folder]);

        page.DisarmCommand.Execute(null);
        page.Alarms.Count.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public async Task An_alarm_whose_picture_does_not_decode_is_listed_without_one()
    {
        await using var app = new TestApp(Quick);
        app.Link.Stream = s =>
        {
            s.Frames.AddRange(Enumerable.Range(0, 200).Select(i => TestPictures.Moving(left: i % 2 == 0)));
            s.Pace = TimeSpan.FromMilliseconds(40);
        };
        var page = new SentryViewModel(app.Parts with { Decode = _ => null }, TimeZoneInfo.Utc);
        await app.ConnectedAsync();

        page.ArmCommand.Execute(null);

        await TestApp.EventuallyAsync(() => page.Alarms.Count > 0);
        page.Alarms[0].Picture.ShouldBeNull();
        page.Dispose();
        page.Alarms.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task An_alarm_whose_picture_was_not_kept_still_says_when()
    {
        await using var app = new TestApp(Quick);
        using var page = Page(app);

        app.Alarms.AlarmRaised(new DateTimeOffset(2026, 10, 5, 21, 40, 7, TimeSpan.Zero), [1], saveSnapshot: false);

        page.LastAlarm.ShouldBe("Movement at 21:40:07.");
    }

    [AvaloniaFact]
    public async Task Closed_it_follows_nothing_and_forgets_its_pictures()
    {
        await using var app = new TestApp(Quick);
        var page = Page(app);
        app.Sentry.Arm();

        page.Dispose();
        app.Sentry.Disarm();

        page.Armed.ShouldBeTrue();
        page.Alarms.ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => new SentryViewModel(null!));
        using var local = new SentryViewModel(app.Parts);
        local.State.ShouldBe("Not armed");
    }
}
