using Avalonia.Headless.XUnit;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Settings;
using Sightline.Platform.Windows.Install;

namespace Sightline.App.Tests.ViewModels;

/// <summary>GitHub's releases, as far as a test needs them.</summary>
internal sealed class FakeUpdates : IUpdateSource
{
    public bool IsInstalled { get; set; } = true;

    public string? Newer { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>When set, a check waits for this before answering, as one over a slow connection does.</summary>
    public Task? Answer { get; set; }

    public int Checks { get; private set; }

    public bool Downloaded { get; private set; }

    public bool Applied { get; private set; }

    public async Task<string?> CheckForNewVersionAsync(CancellationToken cancellationToken)
    {
        Checks++;
        if (Answer is { } answer)
        {
            await answer;
        }

        return Failure is { } failure ? throw failure : Newer;
    }

    public Task DownloadAsync(CancellationToken cancellationToken)
    {
        Downloaded = true;
        return Task.CompletedTask;
    }

    public void ApplyAndRestart() => Applied = true;
}

/// <summary>Finding, downloading and installing a newer Sightline.</summary>
public sealed class UpdatesViewModelTests
{
    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.2.0" };

    private static TestApp App(FakeUpdates? updates, Preferences? preferences = null)
    {
        var app = new TestApp(preferences ?? Returning);
        return updates is null ? app : app.WithUpdates(updates);
    }

    [AvaloniaFact]
    public async Task A_portable_copy_is_sent_to_the_download_page()
    {
        await using var app = App(null);
        using var shell = new ShellViewModel(app.Parts);
        var updates = shell.Updates;

        shell.Start();

        updates.Portable.ShouldBeTrue();
        updates.Status.ShouldStartWith("This copy is the portable one");
        updates.CheckCommand.CanExecute(null).ShouldBeFalse();
        updates.OpenDownloadPageCommand.Execute(null);
        app.Desktop.Links.ShouldBe([UpdatesViewModel.DownloadPage]);
        new UpdatesViewModel(app.Parts with { Updates = new FakeUpdates { IsInstalled = false } }).Portable.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Opening_looks_for_a_newer_version_and_says_when_there_is_none()
    {
        var source = new FakeUpdates();
        await using var app = App(source);
        using var shell = new ShellViewModel(app.Parts);
        shell.Updates.Status.ShouldBe("Sightline looks for a newer version when it opens.");

        shell.Start();

        await TestApp.EventuallyAsync(() => shell.Updates.Status == "Sightline is up to date.");
        source.Checks.ShouldBe(1);
        shell.Updates.Ready.ShouldBeNull();
        shell.Updates.Checking.ShouldBeFalse();
        shell.Settings.Updates.ShouldBeSameAs(shell.Updates);
    }

    [AvaloniaFact]
    public async Task Told_not_to_it_does_not_look()
    {
        var source = new FakeUpdates();
        await using var app = App(source, Returning with { CheckForUpdates = false });
        using var shell = new ShellViewModel(app.Parts);

        shell.Start();
        shell.Settings.CheckForUpdates.ShouldBeFalse();
        shell.Settings.CheckForUpdates = true;

        source.Checks.ShouldBe(0);
        app.Preferences.Current.CheckForUpdates.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_newer_version_is_downloaded_and_installed_by_a_restart()
    {
        var source = new FakeUpdates { Newer = "0.3.0" };
        await using var app = App(source);
        using var shell = new ShellViewModel(app.Parts);
        var updates = shell.Updates;
        var answer = new TaskCompletionSource();
        source.Answer = answer.Task;

        var checking = updates.CheckCommand.ExecuteAsync(null);
        updates.Checking.ShouldBeTrue();
        updates.CheckCommand.CanExecute(null).ShouldBeFalse();
        answer.SetResult();
        await checking;

        source.Downloaded.ShouldBeTrue();
        updates.Ready.ShouldBe("0.3.0");
        updates.Status.ShouldBe("Version 0.3.0 is ready.");
        updates.RestartNote.ShouldBeNull();
        updates.RestartCommand.CanExecute(null).ShouldBeTrue();
        updates.RestartCommand.Execute(null);
        source.Applied.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task The_restart_waits_while_the_camera_records_or_sentry_watches()
    {
        var source = new FakeUpdates { Newer = "0.3.0" };
        await using var app = App(source);
        using var shell = new ShellViewModel(app.Parts);
        var updates = shell.Updates;
        updates.RestartNote.ShouldBeNull();
        await updates.CheckCommand.ExecuteAsync(null);
        await app.ConnectedAsync();

        await app.Controller.ToggleRecording()!;
        await TestApp.EventuallyAsync(() => !updates.RestartCommand.CanExecute(null));
        updates.RestartNote.ShouldStartWith("Sightline will restart to update once");
        await app.Controller.ToggleRecording()!;
        await TestApp.EventuallyAsync(() => updates.RestartCommand.CanExecute(null));

        shell.ToggleSentryCommand.Execute(null);
        updates.RestartCommand.CanExecute(null).ShouldBeFalse();
        shell.ToggleSentryCommand.Execute(null);
        updates.RestartCommand.CanExecute(null).ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_check_that_fails_says_so_and_can_be_tried_again()
    {
        var source = new FakeUpdates { Failure = new HttpRequestException("No such host is known.") };
        await using var app = App(source);
        using var shell = new ShellViewModel(app.Parts);

        await shell.Updates.CheckCommand.ExecuteAsync(null);

        shell.Updates.Status.ShouldBe("Could not look for a newer version: No such host is known.");
        shell.Updates.CheckCommand.CanExecute(null).ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => new UpdatesViewModel(null!));
    }
}
