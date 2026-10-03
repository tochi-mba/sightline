using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.App.Services;
using Sightline.Protocol.GpSock;

namespace Sightline.App.ViewModels;

/// <summary>Where the window is in its life with a camera.</summary>
public enum ConnectionState
{
    /// <summary>Not connected; the connect panel shows.</summary>
    Disconnected,

    /// <summary>Joining the camera's Wi-Fi and opening the control channel.</summary>
    Connecting,

    /// <summary>Connected, and the picture is live.</summary>
    Live,

    /// <summary>Something went wrong; the message says what and what to do.</summary>
    Failed,
}

/// <summary>
/// The window: connecting, the live picture, the shutter, and the camera's settings.
/// </summary>
/// <remarks>
/// Every camera call goes through <see cref="ICameraService"/>, and the two things that need a real
/// UI — decoding a JPEG and getting back onto the UI thread — are passed in, so the whole of this
/// can be tested with a fake camera and no window.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICameraService camera;
    private readonly Func<byte[], Bitmap?> decode;
    private readonly Action<Action> onUiThread;
    private readonly Func<string> downloadFolder;
    private CancellationTokenSource? live;
    private Task? liveTask;
    private byte[]? lastJpeg;

    /// <summary>Creates the view model over a real camera and the real UI thread.</summary>
    public MainViewModel(ICameraService camera)
        : this(
            camera,
            DecodeJpeg,
            action => Dispatcher.UIThread.Post(action),
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Sightline"))
    {
    }

    /// <summary>Creates the view model with the UI plumbing supplied, for tests.</summary>
    public MainViewModel(
        ICameraService camera,
        Func<byte[], Bitmap?> decode,
        Action<Action> onUiThread,
        Func<string>? downloadFolder = null)
    {
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.decode = decode ?? throw new ArgumentNullException(nameof(decode));
        this.onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
        this.downloadFolder = downloadFolder ?? (() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Sightline"));
    }

    /// <summary>Each camera in range, once per adapter that can see it.</summary>
    public ObservableCollection<CameraConnectionOption> Cameras { get; } = [];

    /// <summary>The camera's settings, once connected.</summary>
    public ObservableCollection<SettingViewModel> Settings { get; } = [];

    /// <summary>The files on the camera's card while the library is open.</summary>
    public ObservableCollection<CameraFileViewModel> Files { get; } = [];

    /// <summary>The camera, and the adapter to reach it through.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsNetworkChangeConsent))]
    private CameraConnectionOption? selectedCamera;

    /// <summary>
    /// Whether the person has accepted that the chosen adapter leaves the network it is on.
    /// </summary>
    /// <remarks>
    /// Cleared whenever the choice changes: agreeing to take one adapter off one network is not
    /// agreeing to anything else.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool networkChangeConfirmed;

    /// <summary>Whether the chosen adapter would have to leave a network, so consent is asked for.</summary>
    public bool NeedsNetworkChangeConsent => SelectedCamera?.RequiresConsent == true;

    partial void OnSelectedCameraChanged(CameraConnectionOption? value)
    {
        NetworkChangeConfirmed = false;
        NetworkAdvice = value?.Explanation ?? "";
    }

    /// <summary>The camera's Wi-Fi password, which is on its screen.</summary>
    [ObservableProperty]
    private string password = "12345678";

    /// <summary>What joining will do to this PC's internet, said before it happens.</summary>
    [ObservableProperty]
    private string networkAdvice = "";

    /// <summary>Where the window is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected), nameof(IsLive), nameof(IsConnecting), nameof(IsViewingCamera))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(PhotoCommand), nameof(RecordCommand), nameof(SaveSnapshotCommand), nameof(BrowseFilesCommand), nameof(DownloadFileCommand), nameof(DeleteFileCommand))]
    private ConnectionState state = ConnectionState.Disconnected;

    /// <summary>One sentence about what is happening, or what went wrong and what to do.</summary>
    [ObservableProperty]
    private string message = "Wake the camera's Wi-Fi, then pick it below.";

    /// <summary>The live picture.</summary>
    [ObservableProperty]
    private Bitmap? frame;

    /// <summary>Whether the camera is recording to its card.</summary>
    [ObservableProperty]
    private bool isRecording;

    /// <summary>The picture rate, for the status strip.</summary>
    [ObservableProperty]
    private string frameRate = "";

    /// <summary>A line about the camera itself.</summary>
    [ObservableProperty]
    private string cameraSummary = "";

    /// <summary>Whether the connect panel shows.</summary>
    public bool IsDisconnected => State is ConnectionState.Disconnected or ConnectionState.Failed;

    /// <summary>Whether a connection is being made.</summary>
    public bool IsConnecting => State == ConnectionState.Connecting;

    /// <summary>Whether the picture is live.</summary>
    public bool IsLive => State == ConnectionState.Live;

    /// <summary>Whether the preview rather than the card library is showing.</summary>
    public bool IsViewingCamera => IsLive && !IsBrowsingFiles;

    /// <summary>Whether the card library is showing in place of preview controls.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewingCamera))]
    [NotifyCanExecuteChangedFor(nameof(DownloadFileCommand), nameof(DeleteFileCommand))]
    private bool isBrowsingFiles;

    /// <summary>The card file picked for download or deletion.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadFileCommand), nameof(DeleteFileCommand))]
    private CameraFileViewModel? selectedFile;

    /// <summary>Whether the first delete press has armed the deliberately destructive second one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteButtonText))]
    private bool deleteArmed;

    /// <summary>Delete is a two-press action so a stray click cannot erase the card.</summary>
    public string DeleteButtonText => DeleteArmed ? "Delete permanently — press again" : "Delete selected";

    partial void OnSelectedFileChanged(CameraFileViewModel? value) => DeleteArmed = false;

    /// <summary>
    /// Looks for cameras on every adapter, without changing any connection, and offers the choice that
    /// changes least first.
    /// </summary>
    [RelayCommand]
    public async Task RefreshCamerasAsync()
    {
        var previous = SelectedCamera;
        IReadOnlyList<CameraConnectionOption> found;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            found = await camera.FindCamerasAsync(timeout.Token);
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            Message = $"Could not look for cameras: {Explain(exception)}";
            return;
        }

        Cameras.Clear();
        foreach (var option in found)
        {
            Cameras.Add(option);
        }

        // Keep the person's choice when it is still there; otherwise the first, which never needs
        // an adapter to leave a network when any other way exists.
        SelectedCamera = Cameras.FirstOrDefault(o => o.Ssid == previous?.Ssid && o.AdapterId == previous.AdapterId)
            ?? Cameras.FirstOrDefault();
        if (State != ConnectionState.Live)
        {
            Message = Cameras.Count == 0
                ? "No camera in range. Press its Wi-Fi button, then Refresh."
                : "Pick the camera and the Wi-Fi adapter to reach it through, then connect.";
        }
    }

    private bool CanConnect() =>
        SelectedCamera is not null
        && (!SelectedCamera.RequiresConsent || NetworkChangeConfirmed)
        && State != ConnectionState.Connecting && State != ConnectionState.Live;

    /// <summary>Joins the camera and starts the picture.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        if (SelectedCamera is not { } option)
        {
            return;
        }

        var ssid = option.Ssid;

        State = ConnectionState.Connecting;
        Message = $"Connecting to {ssid}...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await camera.ConnectAsync(option, Password, NetworkChangeConfirmed, timeout.Token);
            State = ConnectionState.Live;
            IsBrowsingFiles = false;
            Message = "Live.";

            // The picture first: it is what somebody connected to see, and on the reference
            // camera reading the menu before the stream had started left RTSP unanswered.
            StartLive();
            await LoadCameraAsync(timeout.Token);
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            await StopLiveAsync();
            await camera.DisconnectAsync();
            Fail(exception);
        }
    }

    /// <summary>Stops the picture, ends the session and leaves the camera's Wi-Fi.</summary>
    [RelayCommand]
    public async Task DisconnectAsync()
    {
        await StopLiveAsync();
        await camera.DisconnectAsync();
        Settings.Clear();
        Files.Clear();
        SelectedFile = null;
        IsBrowsingFiles = false;
        Frame = null;
        IsRecording = false;
        FrameRate = "";
        CameraSummary = "";
        lastJpeg = null;
        State = ConnectionState.Disconnected;
        Message = "Disconnected. Your PC is back on its usual network.";
    }

    private bool CanUseCamera() => State == ConnectionState.Live;

    /// <summary>Takes a photo onto the camera's card.</summary>
    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    public async Task PhotoAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await camera.PhotoAsync(timeout.Token);
            Message = "Photo taken. It is on the camera's card.";
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            Message = Explain(exception);
        }
    }

    /// <summary>Starts or stops recording to the camera's card.</summary>
    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    public async Task RecordAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            IsRecording = await camera.ToggleRecordingAsync(timeout.Token);
            Message = IsRecording ? "Recording to the camera's card." : "Recording stopped.";
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            Message = Explain(exception);
        }
    }

    /// <summary>Pauses preview cleanly, then lists the card without holding a half-open RTSP session.</summary>
    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    public async Task BrowseFilesAsync()
    {
        await StopLiveAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var found = await camera.FilesAsync(timeout.Token);
            Files.Clear();
            foreach (var file in found)
            {
                Files.Add(new CameraFileViewModel(file));
            }

            SelectedFile = Files.FirstOrDefault();
            IsBrowsingFiles = true;
            Message = Files.Count == 0
                ? "The card is empty, missing, or could not be read by the camera."
                : $"{Files.Count} file{(Files.Count == 1 ? "" : "s")} on the camera's card.";
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            Message = $"Could not read the card: {Explain(exception)}";
            IsBrowsingFiles = false;
            StartLive();
        }
    }

    /// <summary>Leaves the card library and starts a fresh, properly-owned preview session.</summary>
    [RelayCommand]
    public void ReturnToLive()
    {
        if (!IsLive || !IsBrowsingFiles)
        {
            return;
        }

        DeleteArmed = false;
        IsBrowsingFiles = false;
        Message = "Live.";
        StartLive();
    }

    private bool CanUseSelectedFile() => IsLive && IsBrowsingFiles && SelectedFile is not null;

    /// <summary>Copies the selected file into Downloads\Sightline without replacing an existing file.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSelectedFile))]
    public async Task DownloadFileAsync()
    {
        if (SelectedFile is not { } selected)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var progress = new CallbackProgress<long>(bytes => onUiThread(() =>
                Message = $"Downloading {selected.Name}… {bytes / (1024d * 1024d):0.0} MB"));
            var path = await camera.DownloadAsync(selected.File, downloadFolder(), progress, timeout.Token);
            Message = $"Saved {Path.GetFileName(path)} to Downloads\\Sightline.";
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            Message = $"Download stopped: {Explain(exception)}";
        }
    }

    /// <summary>Arms deletion on the first press and permanently deletes on the second.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSelectedFile))]
    public async Task DeleteFileAsync()
    {
        if (SelectedFile is not { } selected)
        {
            return;
        }

        if (!DeleteArmed)
        {
            DeleteArmed = true;
            Message = $"Press delete again to permanently remove {selected.Name} from the card.";
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await camera.DeleteAsync(selected.File, timeout.Token);
            Files.Remove(selected);
            SelectedFile = Files.FirstOrDefault();
            DeleteArmed = false;
            Message = $"Deleted {selected.Name} from the camera's card.";
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            DeleteArmed = false;
            Message = $"{selected.Name} was not deleted: {Explain(exception)}";
        }
    }

    /// <summary>Saves the picture on screen to Pictures\Sightline.</summary>
    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    public async Task SaveSnapshotAsync()
    {
        if (lastJpeg is not { } jpeg)
        {
            Message = "No picture yet to save.";
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sightline");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"sightline-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
        await File.WriteAllBytesAsync(path, jpeg);
        Message = $"Saved {Path.GetFileName(path)} to Pictures\\Sightline.";
    }

    /// <summary>Called by a setting row when its value changes.</summary>
    internal async Task WriteSettingAsync(SettingViewModel setting, int value)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await camera.SetSettingAsync(setting.Id, value, timeout.Token);
            Message = $"{setting.Name} set to {setting.LabelFor(value)}.";
        }
        catch (Exception exception) when (IsCameraTrouble(exception))
        {
            Message = $"{setting.Name} was not changed: {Explain(exception)}";
        }
    }

    private async Task LoadCameraAsync(CancellationToken cancellationToken)
    {
        var status = await camera.StatusAsync(cancellationToken);
        IsRecording = status.IsRecording;
        var menu = await camera.MenuAsync(cancellationToken);

        Settings.Clear();
        foreach (var setting in menu.Settings.Where(s => s.Kind == MenuSettingKind.Choice))
        {
            Settings.Add(new SettingViewModel(setting, this));
        }

        var version = menu.Find(0x0209)?.Choices.FirstOrDefault().Label;
        CameraSummary = $"{status.Mode} mode · {(status.OnExternalPower ? "on external power" : "on battery")}"
            + (version is null ? "" : $" · firmware {version}");
    }

    private void StartLive()
    {
        if (live is not null)
        {
            return;
        }

        live = new CancellationTokenSource();
        var token = live.Token;
        liveTask = Task.Run(() => RunLiveAsync(token), CancellationToken.None);
    }

    private async Task RunLiveAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var frames = 0;
        try
        {
            await foreach (var cameraFrame in camera.LiveAsync(cancellationToken))
            {
                var bitmap = decode(cameraFrame.Jpeg);
                var jpeg = cameraFrame.Jpeg;
                frames++;
                string? rate = null;
                if (clock.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    rate = $"{frames / clock.Elapsed.TotalSeconds:0.0} fps · {cameraFrame.Width}×{cameraFrame.Height}";
                    frames = 0;
                    clock.Restart();
                }

                onUiThread(() =>
                {
                    var previous = Frame;
                    Frame = bitmap;
                    lastJpeg = jpeg;
                    previous?.Dispose();
                    if (rate is not null)
                    {
                        FrameRate = rate;
                    }
                });
            }

            onUiThread(() => Message = "The camera stopped sending. Its Wi-Fi may have gone to sleep.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped on purpose.
        }
        catch (Exception exception)
        {
            // Deliberately broad. This loop runs on a task nobody awaits, so anything not caught
            // here vanishes: the picture stays black and nothing says why, which is exactly how
            // the first build of this window failed.
            onUiThread(() => Message = $"The picture stopped: {Explain(exception)}");
        }
    }

    /// <summary>
    /// Stops the picture and waits until the camera has been told the session is over.
    /// </summary>
    /// <remarks>
    /// The wait is the point. Cancelling only asks the loop to stop; its RTSP TEARDOWN is sent on
    /// the way out. Closing the control channel or the app before that lands abandons the session,
    /// and the reference camera's RTSP server then answers nobody until it is power-cycled.
    /// </remarks>
    private async Task StopLiveAsync()
    {
        if (live is null)
        {
            return;
        }

        await live.CancelAsync();
        if (liveTask is { } running)
        {
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(4)));
        }

        live.Dispose();
        live = null;
        liveTask = null;
    }

    private void Fail(Exception exception)
    {
        State = ConnectionState.Failed;
        Message = Explain(exception);
    }

    private static bool IsCameraTrouble(Exception exception) =>
        exception is IOException or TimeoutException or OperationCanceledException
            or System.Net.Sockets.SocketException or InvalidOperationException
            or GpSockProtocolException or GpSockRefusedException
            or Sightline.Protocol.Rtp.RtspException or Sightline.Core.CameraNotReachableException
            or Sightline.Core.Connectivity.CameraLinkException or Sightline.Core.Connectivity.WlanException;

    /// <summary>An exception as a sentence a person can act on.</summary>
    public static string Explain(Exception exception) => exception switch
    {
        Sightline.Core.CameraNotReachableException e => e.Message,
        Sightline.Core.Connectivity.CameraLinkException e => e.Message,
        Sightline.Core.Connectivity.WlanException e => e.Message,
        GpSockRefusedException e => e.Message,
        OperationCanceledException or TimeoutException =>
            "The camera did not answer in time. Its Wi-Fi switches off after about a minute; press its Wi-Fi button and try again.",
        System.Net.Sockets.SocketException =>
            "Could not reach the camera. Make sure its Wi-Fi is on and it is close by.",
        _ => exception.Message,
    };

    private static Bitmap? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            // One bad frame is not worth stopping the picture for; the next one replaces it.
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs when the window closes, and ends the session properly rather than letting the process
    /// exit with it open — see <see cref="StopLiveAsync"/> for why that matters to the camera.
    /// </remarks>
    public void Dispose()
    {
        Task.Run(async () =>
        {
            await StopLiveAsync().ConfigureAwait(false);
            await camera.DisconnectAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
        Frame?.Dispose();
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
