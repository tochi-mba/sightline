using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sightline.App.ViewModels;

/// <summary>
/// Newer versions: looked for on GitHub when the app opens, if the person allows it, downloaded quietly,
/// and installed by a restart the person chooses, never while the camera records or Sentry watches.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    /// <summary>Where the downloads are, for a copy that cannot update itself.</summary>
    public const string DownloadPage = "https://tochi-mba.github.io/sightline/";

    private readonly AppParts parts;
    private bool held;

    /// <summary>Creates the updates section.</summary>
    public UpdatesViewModel(AppParts parts)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        status = Portable
            ? "This copy is the portable one, which cannot update itself. Newer versions are on the download page."
            : "Sightline looks for a newer version when it opens.";
    }

    /// <summary>Whether this copy cannot update itself: the portable exe, or a build run from source.</summary>
    public bool Portable => parts.Updates is not { IsInstalled: true };

    /// <summary>Where updating stands, in a sentence.</summary>
    [ObservableProperty]
    private string status;

    /// <summary>Whether a check is under way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool checking;

    /// <summary>The version downloaded and waiting for a restart; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartNote))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    private string? ready;

    /// <summary>Why the restart waits, when it does.</summary>
    public string? RestartNote => Ready is not null && held
        ? "Sightline will restart to update once the camera stops recording and Sentry is disarmed."
        : null;

    /// <summary>Holds the restart while something must not be cut off: a recording, or a Sentry watch.</summary>
    internal void Hold(bool busy)
    {
        if (held == busy)
        {
            return;
        }

        held = busy;
        OnPropertyChanged(nameof(RestartNote));
        RestartCommand.NotifyCanExecuteChanged();
    }

    private bool CanCheck() => !Checking && !Portable;

    /// <summary>Asks GitHub for a newer version and downloads it; a failure is said, never thrown.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        var source = parts.Updates!;
        Checking = true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Status = "Looking for a newer version…";
            if (await source.CheckForNewVersionAsync(limit.Token) is not { } version)
            {
                Status = "Sightline is up to date.";
                return;
            }

            Status = $"Downloading version {version}…";
            await source.DownloadAsync(limit.Token);
            Ready = version;
            Status = $"Version {version} is ready.";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Offline, rate-limited or interrupted: an update check is never what stops the app working.
            Status = $"Could not look for a newer version: {failure.Message}";
        }
        finally
        {
            Checking = false;
        }
    }

    private bool CanRestart() => Ready is not null && !held;

    /// <summary>Installs the downloaded version and starts it.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart() => parts.Updates!.ApplyAndRestart();

    [RelayCommand]
    private void OpenDownloadPage() => parts.Desktop.OpenLink(DownloadPage);
}
