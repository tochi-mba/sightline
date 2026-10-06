using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.Core.Camera;
using Sightline.Core.Settings;

namespace Sightline.App.ViewModels;

/// <summary>
/// The live picture, the shutter, the mode, and a snapshot of what is on screen.
/// </summary>
/// <remarks>
/// Pictures are decoded on the stream's thread, and one that arrives while the last is still waiting to be
/// shown is skipped rather than queued: a picture that falls behind catches up by skipping to the newest,
/// never by showing old ones.
/// </remarks>
public sealed partial class LiveViewModel : ObservableObject, IDisposable
{
    /// <summary>Who holds the live view open while this page shows.</summary>
    internal const string Holder = "window";

    private readonly AppParts parts;
    private byte[]? lastJpeg;
    private int showing;

    /// <summary>Creates the page.</summary>
    public LiveViewModel(AppParts parts)
    {
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        parts.Controller.FrameArrived += OnFrame;
        parts.Preferences.Changed += OnPreferences;
    }

    /// <summary>The newest picture, or null before the first.</summary>
    [ObservableProperty]
    private Bitmap? picture;

    /// <summary>Why the picture is not simply playing, or null when it is.</summary>
    [ObservableProperty]
    private string? message;

    /// <summary>What the camera is doing, or what its card still holds.</summary>
    [ObservableProperty]
    private string status = "";

    /// <summary>What the shutter does now.</summary>
    [ObservableProperty]
    private string shutterLabel = "Start recording";

    /// <summary>Whether the camera is recording to its card.</summary>
    [ObservableProperty]
    private bool recording;

    /// <summary>Whether the shutter takes photos.</summary>
    [ObservableProperty]
    private bool photos;

    /// <summary>Whether the camera takes a command now: connected, and not busy with another.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShutterCommand), nameof(UseVideoCommand), nameof(UsePhotosCommand))]
    private bool ready;

    /// <summary>The picture rate, when the person asked to see it.</summary>
    [ObservableProperty]
    private string frameRate = "";

    /// <summary>What happened to the last snapshot.</summary>
    [ObservableProperty]
    private string? snapshot;

    /// <summary>Whether the picture covers the area, cropped, rather than fits inside it.</summary>
    public bool Fill => parts.Preferences.Current.Fit == PictureFit.Fill;

    /// <summary>Whether the picture is turned round, for a camera mounted upside down.</summary>
    public bool Flip => parts.Preferences.Current.Flip;

    /// <summary>Whether the picture is mirrored.</summary>
    public bool Mirror => parts.Preferences.Current.Mirror;

    /// <summary>The framing guide drawn over the picture.</summary>
    public GridOverlay Grid => parts.Preferences.Current.Grid;

    /// <summary>Holds the live view open while the page shows, and lets go when it does not.</summary>
    public void Shown(bool shown)
    {
        if (shown)
        {
            parts.Controller.HoldLive(Holder);
        }
        else
        {
            parts.Controller.ReleaseLive(Holder);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        parts.Controller.FrameArrived -= OnFrame;
        parts.Preferences.Changed -= OnPreferences;
        parts.Controller.ReleaseLive(Holder);
        Picture?.Dispose();
        Picture = null;
    }

    /// <summary>Follows the camera.</summary>
    internal void Apply(CameraState state)
    {
        Message = CameraWords.Picture(state);
        Status = CameraWords.Status(state);
        ShutterLabel = CameraWords.Shutter(state);
        Recording = state.IsRecording;
        Photos = state.Mode == CaptureMode.Photo;
        Ready = state.IsConnected && state.Task is null;
        FrameRate = parts.Preferences.Current.ShowFrameRate && state.Live is LiveView.Playing { FramesPerSecond: > 0 } playing
            ? playing.FramesPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " fps"
            : "";
        if (state.Connection is Connection.Idle or Connection.Failed)
        {
            // Nothing stale is left on screen once the camera is gone.
            Show(null);
            Volatile.Write(ref lastJpeg, null);
        }
    }

    private void OnFrame(LiveFrame frame)
    {
        Volatile.Write(ref lastJpeg, frame.Jpeg);
        if (Interlocked.Exchange(ref showing, 1) == 1)
        {
            return;
        }

        var decoded = parts.Decode(frame.Jpeg);
        parts.Post(() =>
        {
            if (decoded is not null)
            {
                Show(decoded);
            }

            Volatile.Write(ref showing, 0);
        });
    }

    private void Show(Bitmap? next)
    {
        var previous = Picture;
        Picture = next;
        if (!ReferenceEquals(previous, next))
        {
            previous?.Dispose();
        }
    }

    private void OnPreferences(Preferences preferences) => parts.Post(() =>
    {
        OnPropertyChanged(nameof(Fill));
        OnPropertyChanged(nameof(Flip));
        OnPropertyChanged(nameof(Mirror));
        OnPropertyChanged(nameof(Grid));
        Apply(parts.Controller.State);
    });

    [RelayCommand(CanExecute = nameof(Ready))]
    private void Shutter() => parts.Controller.Shutter();

    [RelayCommand(CanExecute = nameof(Ready))]
    private void UseVideo() => parts.Controller.SwitchMode(CaptureMode.Video);

    [RelayCommand(CanExecute = nameof(Ready))]
    private void UsePhotos() => parts.Controller.SwitchMode(CaptureMode.Photo);

    /// <summary>Saves the picture on screen, exactly as the camera sent it.</summary>
    [RelayCommand]
    private void SaveSnapshot()
    {
        if (Volatile.Read(ref lastJpeg) is not { } jpeg)
        {
            Snapshot = "No picture yet to save.";
            return;
        }

        try
        {
            Directory.CreateDirectory(parts.SnapshotFolder);
            var name = "sightline-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jpg";
            var path = FolderSink.AvailablePath(parts.SnapshotFolder, name);
            File.WriteAllBytes(path, jpeg);
            Snapshot = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Snapshot = $"The snapshot was not saved: {failure.Message}";
        }
    }
}
