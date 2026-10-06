using System.Collections.Immutable;
using System.Diagnostics;
using Sightline.Protocol.GpSock;

namespace Sightline.Core.Camera;

/// <summary>How long the controller gives the camera.</summary>
/// <param name="Answer">For one request to be answered.</param>
/// <param name="LongAnswer">For a request answered in many frames: the menu, or the card's file list.</param>
/// <param name="TransferStall">For a download to go without a byte before it is called stalled.</param>
/// <param name="StatusInterval">Between status polls while connected.</param>
/// <param name="ReconnectDelay">After a camera is lost, times the attempt number, before trying again.</param>
/// <param name="ReconnectAttempts">How many times a lost camera is tried before giving up.</param>
public sealed record ControllerTiming(
    TimeSpan Answer,
    TimeSpan LongAnswer,
    TimeSpan TransferStall,
    TimeSpan StatusInterval,
    TimeSpan ReconnectDelay,
    int ReconnectAttempts)
{
    /// <summary>The timings the app uses, the same as the Android app's.</summary>
    public static ControllerTiming Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        5);
}

/// <summary>
/// The camera, as the Windows app drives it: connecting and staying connected, the live view, the shutter,
/// settings and the card. The port of the Android app's controller, held to the same rules:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>One camera operation at a time.</b> A shutter press, a setting and a status poll all go down one
/// control channel, and an operation that switches the camera's mode must finish before another starts.</item>
/// <item><b>The card is read with the live view stopped.</b> The camera refuses to browse while it streams.</item>
/// <item><b>Nothing waits on the camera forever.</b> A half-asleep camera accepts a connection and then
/// answers nothing, so every request has a deadline, a download a stall detector, and a camera that stops
/// answering is treated as lost and reconnected.</item>
/// </list>
/// <para>
/// State changes are serialised by one lock and announced through <see cref="StateChanged"/>, from whatever
/// thread made them; the window marshals onto its own.
/// </para>
/// </remarks>
public sealed class CameraController : IAsyncDisposable
{
    private readonly ICameraLink link;
    private readonly ControllerTiming timing;
    private readonly CameraSessionTiming? sessionTiming;
    private readonly Func<bool> reconnects;
    private readonly Lock gate = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly HashSet<string> liveHolders = [];

    // Holders whose holding is itself the person asking for the picture, such as an armed Sentry.
    private readonly HashSet<string> askingHolders = [];
    private CameraState state = CameraState.Initial;
    private Run? staying;
    private Connected? current;
    private CancellationTokenSource? live;
    private Task? liveTask;
    private bool browsing;

    // Asked for by the person on the live page; and whether a picture has arrived since. Both are cleared
    // when the person disconnects, and kept through a reconnect.
    private bool pictureAsked;
    private bool pictureRan;
    private CameraMode? cameraMode;
    private long frameNumber;
    private long noticeNumber;

    /// <summary>Creates a controller joining cameras through <paramref name="link"/>.</summary>
    /// <param name="link">Puts this PC on the camera's network.</param>
    /// <param name="timing">How long the camera is given; the real values when omitted.</param>
    /// <param name="sessionTiming">How long the stream is given; the real values when omitted.</param>
    /// <param name="reconnects">Whether a lost camera is tried again, read each time one is lost.</param>
    public CameraController(
        ICameraLink link,
        ControllerTiming? timing = null,
        CameraSessionTiming? sessionTiming = null,
        Func<bool>? reconnects = null)
    {
        this.link = link ?? throw new ArgumentNullException(nameof(link));
        this.timing = timing ?? ControllerTiming.Default;
        this.sessionTiming = sessionTiming;
        this.reconnects = reconnects ?? (() => true);
    }

    /// <summary>Everything the window shows about the camera.</summary>
    public CameraState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>Raised after every change to <see cref="State"/>, with the new state.</summary>
    public event Action<CameraState>? StateChanged;

    /// <summary>Raised for each live picture; apart from <see cref="StateChanged"/> so twelve a second do not redraw the window.</summary>
    public event Action<LiveFrame>? FrameArrived;

    /// <summary>
    /// Connects and stays connected until <see cref="DisconnectAsync"/>, reconnecting when the camera is lost.
    /// Ignored while a connection is already being made or held.
    /// </summary>
    /// <returns>The connection's whole life, which ends when it fails for good or is given up.</returns>
    public Task Connect()
    {
        lock (gate)
        {
            if (staying is { Task.IsCompleted: false } running)
            {
                return running.Task;
            }

            var cancel = new CancellationTokenSource();
            var token = cancel.Token;
            staying = new Run(cancel, Task.Run(() => StayConnectedAsync(token), CancellationToken.None));
            return staying.Task;
        }
    }

    /// <summary>Leaves the camera and its network. The camera stops recording when its control channel closes.</summary>
    public async Task DisconnectAsync()
    {
        Run? running;
        lock (gate)
        {
            running = staying;
            staying = null;
        }

        if (running is not null)
        {
            await running.Cancel.CancelAsync().ConfigureAwait(false);
            await running.Task.ConfigureAwait(false);
            running.Cancel.Dispose();
        }

        bool ran;
        lock (gate)
        {
            ran = pictureRan;
            pictureAsked = false;
            pictureRan = false;
        }

        Update(s => CameraState.Initial with { Mode = s.Mode });
        if (ran)
        {
            // The camera is left with its buttons stuck; the person should hear it here, not find it out.
            PostNotice(CameraWords.ButtonsStuck);
        }
    }

    /// <summary>
    /// Starts the live picture for whoever holds it, now that the person has asked: the camera gives it once
    /// each time it starts, and its own buttons stay stuck once it has run, so it is never started unasked.
    /// </summary>
    public void ShowLivePicture()
    {
        lock (gate)
        {
            pictureAsked = true;
        }

        UpdateLive();
    }

    /// <summary>
    /// Holds the live view open on behalf of <paramref name="holder"/>. It runs while anybody holds it, once
    /// the picture has been asked for: by <see cref="ShowLivePicture"/>, or by a holder that is
    /// <paramref name="asking"/>, whose holding is itself the person's request, as arming Sentry is.
    /// </summary>
    public void HoldLive(string holder, bool asking = false)
    {
        lock (gate)
        {
            liveHolders.Add(holder);
            if (asking)
            {
                askingHolders.Add(holder);
            }
        }

        UpdateLive();
    }

    /// <summary>Lets go of the live view on behalf of <paramref name="holder"/>.</summary>
    public void ReleaseLive(string holder)
    {
        lock (gate)
        {
            liveHolders.Remove(holder);
            askingHolders.Remove(holder);
        }

        UpdateLive();
    }

    /// <summary>Takes a photo or starts or stops recording, whichever the mode says.</summary>
    public Task? Shutter() => State.Mode == CaptureMode.Video ? ToggleRecording() : TakePhoto();

    /// <summary>Takes a photo onto the camera's card.</summary>
    public Task? TakePhoto() => Perform(CameraTask.TakingPhoto, async connected =>
    {
        RefuseWhileRecording("Stop recording to take a photo.");
        await EnterModeAsync(connected, CameraMode.Capture).ConfigureAwait(false);
        await AskAsync(connected, (c, t) => c.CapturePictureAsync(t)).ConfigureAwait(false);
        await ReadStatusAsync(connected).ConfigureAwait(false);
        PostNotice("Photo saved to the camera's card.");
    });

    /// <summary>Starts or stops recording, and reports what the camera then says it is doing.</summary>
    public Task? ToggleRecording()
    {
        var starting = !State.IsRecording;
        return Perform(starting ? CameraTask.StartingRecording : CameraTask.StoppingRecording, async connected =>
        {
            if (starting)
            {
                // A recording is video, whichever button started it: the notification's and Sentry's
                // can start one from photo mode, and the app must not go on calling it photo mode.
                await EnterModeAsync(connected, CameraMode.Record).ConfigureAwait(false);
                Update(s => s with { Mode = CaptureMode.Video });
            }

            await AskAsync(connected, (c, t) => c.ToggleRecordingAsync(t)).ConfigureAwait(false);
            var recording = (await ReadStatusAsync(connected).ConfigureAwait(false)).IsRecording;
            PostNotice((starting, recording) switch
            {
                (true, true) => "Recording to the camera's card.",
                (true, false) => "The camera did not start recording.",
                (false, true) => "The camera is still recording.",
                _ => "Recording stopped and saved to the card.",
            });
        });
    }

    /// <summary>Switches between video and photos; before connecting only the shutter's action is chosen.</summary>
    public Task? SwitchMode(CaptureMode mode)
    {
        var now = State;
        if (mode == now.Mode)
        {
            return null;
        }

        if (!now.IsConnected)
        {
            Update(s => s with { Mode = mode });
            return null;
        }

        return Perform(CameraTask.SwitchingMode, async connected =>
        {
            RefuseWhileRecording($"Stop recording to switch to {(mode == CaptureMode.Video ? "videos" : "photos")}.");
            await EnterModeAsync(connected, mode.CameraMode()).ConfigureAwait(false);
            Update(s => s with { Mode = mode });
        });
    }

    /// <summary>Sets a choice setting, then reads it back: the camera acknowledges values it ignores.</summary>
    public Task? ChangeSetting(int id, int value) => Perform(CameraTask.ChangingSetting, async connected =>
    {
        RefuseWhileRecording("Settings cannot change while the camera records.");
        var setting = State.Settings.FirstOrDefault(s => s.Menu.Id == id);
        if (setting is null || !setting.IsChangeable || setting.Menu.Choices.All(c => c.Value != value))
        {
            throw new RefusalException("The camera does not offer that setting.");
        }

        await AskAsync(connected, (c, t) => c.SetSettingAsync(id, value, t)).ConfigureAwait(false);
        var kept = await AskAsync(connected, (c, t) => c.GetChoiceAsync(id, t)).ConfigureAwait(false);
        Update(s => s with { Settings = s.Settings.Select(x => x.Menu.Id == id ? x with { Value = kept } : x).ToImmutableList() });
        if (kept != value)
        {
            PostNotice($"The camera kept {setting.Menu.Name} at {setting.Menu.LabelFor(kept)}.");
        }

        await ReadStatusAsync(connected).ConfigureAwait(false);
    });

    /// <summary>Reads the card's file list, then each thumbnail, newest first.</summary>
    public Task? RefreshLibrary() => Perform(CameraTask.ReadingCard, async connected =>
    {
        RefuseWhileRecording("Stop recording to look at the card.");
        Update(s => s with { Library = s.Library with { Reading = true } });
        try
        {
            await BrowseAsync(connected, async () =>
            {
                var files = (await AskAsync(connected, (c, t) => c.GetFileListAsync(t), timing.LongAnswer).ConfigureAwait(false)).ToImmutableList();
                SetFiles(files);
                foreach (var file in Enumerable.Reverse(files))
                {
                    if (!State.Library.Thumbnails.ContainsKey(file))
                    {
                        await FetchThumbnailAsync(connected, file).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            Update(s => s with { Library = s.Library with { Reading = false } });
        }
    });

    /// <summary>
    /// Copies <paramref name="files"/> into <paramref name="sink"/>, each published only once whole; a file that
    /// fails is marked and the rest still copied, unless the camera itself was lost.
    /// </summary>
    /// <param name="files">What to copy.</param>
    /// <param name="sink">Where it goes.</param>
    /// <param name="deleteAfter">Whether each file safely copied is then deleted from the card.</param>
    public Task? Download(IReadOnlyList<CameraFile> files, IMediaSink sink, bool deleteAfter = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sink);
        return Perform(CameraTask.Copying, async connected =>
        {
            RefuseWhileRecording("Stop recording to copy from the card.");
            UpdateTransfers(files.ToDictionary(f => f, _ => (Transfer)Transfer.Queued.Instance));
            try
            {
                await BrowseAsync(connected, async () =>
                {
                    foreach (var file in files)
                    {
                        await CopyAsync(connected, file, sink).ConfigureAwait(false);
                    }

                    if (deleteAfter)
                    {
                        await DeleteCopiedAsync(connected, files).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                var stopped = State.Library.Transfers
                    .Where(t => t.Value is Transfer.Queued or Transfer.Copying)
                    .ToDictionary(t => t.Key, _ => (Transfer)new Transfer.Failed("Not copied: the copy was stopped."));
                UpdateTransfers(stopped);
            }
        });
    }

    /// <summary>Deletes <paramref name="files"/> from the card, highest index first, then reads the card again.</summary>
    public Task? Delete(IReadOnlyList<CameraFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Perform(CameraTask.Deleting, async connected =>
        {
            RefuseWhileRecording("Stop recording to delete from the card.");
            await BrowseAsync(connected, async () =>
            {
                foreach (var file in files.OrderByDescending(f => f.Index))
                {
                    await AskAsync(connected, (c, t) => c.DeleteFileAsync(file.Index, t)).ConfigureAwait(false);
                }

                SetFiles((await AskAsync(connected, (c, t) => c.GetFileListAsync(t), timing.LongAnswer).ConfigureAwait(false)).ToImmutableList());
            }).ConfigureAwait(false);
            PostNotice(files.Count == 1 ? "Deleted from the card." : $"{files.Count} files deleted from the card.");
        });
    }

    /// <summary>Clears the notice numbered <paramref name="id"/>, unless a newer one has replaced it.</summary>
    public void DismissNotice(long id) => Update(s => s.Notice?.Id == id ? s with { Notice = null } : s);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        operations.Dispose();
    }

    private async Task StayConnectedAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        Problem? problem = null;
        try
        {
            while (true)
            {
                if (problem is not null)
                {
                    if (attempt >= timing.ReconnectAttempts)
                    {
                        Update(s => s with { Connection = new Connection.Failed(problem) });
                        return;
                    }

                    attempt++;
                    var reconnecting = new Connection.Reconnecting(attempt, timing.ReconnectAttempts, problem);
                    Update(s => s with { Connection = reconnecting });
                    await Task.Delay(timing.ReconnectDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Update(s => s with { Connection = Connection.Joining.Instance });
                }

                var (connected, outcome) = await ConnectOnceAsync(problem is not null, cancellationToken).ConfigureAwait(false);
                if (!connected)
                {
                    if (problem is null)
                    {
                        // The first attempt failing is shown at once: the person is there, waiting.
                        Update(s => s with { Connection = new Connection.Failed(outcome) });
                        return;
                    }

                    problem = outcome;
                    continue;
                }

                if (!reconnects())
                {
                    Update(s => s with { Connection = new Connection.Failed(outcome) });
                    return;
                }

                attempt = 0;
                problem = outcome;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disconnected on purpose.
        }
    }

    /// <returns>Whether it connected at all, and the problem that ended it.</returns>
    private async Task<(bool Connected, Problem Problem)> ConnectOnceAsync(bool reconnecting, CancellationToken cancellationToken)
    {
        ICameraLease lease;
        try
        {
            lease = await link.JoinAsync(reconnecting, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, Problem.WhileJoining(failure));
        }

        try
        {
            return await HoldAsync(lease, reconnecting, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Opens the camera on <paramref name="lease"/>'s network and holds it until it is lost.</summary>
    private async Task<(bool Connected, Problem Problem)> HoldAsync(ICameraLease lease, bool reconnecting, CancellationToken cancellationToken)
    {
        if (!reconnecting)
        {
            Update(s => s with { Connection = Connection.Opening.Instance });
        }

        CameraSession session;
        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            opening.CancelAfter(timing.Answer);
            try
            {
                session = await CameraSession.OpenAsync(lease.Transport, CameraAddress.Default.ToString(), sessionTiming, opening.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, new Problem(
                    ProblemKind.NoAnswer,
                    $"The camera did not answer on its control port within {timing.Answer.TotalSeconds:0} seconds."));
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, Problem.WhileTalking(failure));
            }
        }

        var connected = new Connected(session, lease, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        try
        {
            try
            {
                await ReadCameraAsync(connected).ConfigureAwait(false);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, Problem.WhileTalking(failure));
            }

            lock (gate)
            {
                current = connected;
            }

            Update(s => s with { Connection = Connection.Connected.Instance });
            UpdateLive();
            return (true, await WatchAsync(connected, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            lock (gate)
            {
                current = null;
            }

            // Watching stops first, so nothing reports the stream's end as a failure; then the operations are
            // cancelled, so one waiting on the camera ends as cancelled rather than as a camera that failed;
            // then the session closes, stream and control channel, which ends any request that ignored the
            // cancelling; and only then are the operations waited for.
            await StopLiveAsync().ConfigureAwait(false);
            await connected.Work.CancelAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            await connected.WaitForOperationsAsync().ConfigureAwait(false);
            connected.Work.Dispose();
        }
    }

    private async Task ReadCameraAsync(Connected connected)
    {
        var status = await ReadStatusAsync(connected).ConfigureAwait(false);
        var mode = status.Mode switch
        {
            CameraMode.Capture => CaptureMode.Photo,
            CameraMode.Record => CaptureMode.Video,
            // Left browsing, or in a mode this app does not know: put it where the person last was.
            _ => State.Mode,
        };
        await EnterModeAsync(connected, mode.CameraMode()).ConfigureAwait(false);

        var menu = await AskAsync(connected, (c, t) => c.GetMenuAsync(t), timing.LongAnswer).ConfigureAwait(false);
        var settings = new List<CameraSetting>();
        foreach (var setting in menu.Settings)
        {
            settings.Add(setting.Kind switch
            {
                MenuSettingKind.Choice => new CameraSetting(setting, await ReadChoiceAsync(connected, setting.Id).ConfigureAwait(false), null),
                MenuSettingKind.Text or MenuSettingKind.ReadOnly => new CameraSetting(setting, null, await ReadTextAsync(connected, setting.Id).ConfigureAwait(false)),
                _ => new CameraSetting(setting, null, null),
            });
        }

        var name = settings.FirstOrDefault(s => s.Menu.Id == MenuIds.WifiName)?.Text;
        Update(s => s with { Mode = mode, Settings = [.. settings], CameraName = name });
    }

    /// <summary>Holds the connection until it is lost, polling the camera's status meanwhile.</summary>
    private async Task<Problem> WatchAsync(Connected connected, CancellationToken cancellationToken)
    {
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loss = connected.Lease.Lost.ContinueWith(
            _ => connected.Lose(new Problem(ProblemKind.Lost, "Windows lost the camera's network.")),
            watching.Token,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var polling = PollAsync(connected, watching.Token);
        var lost = await connected.LostTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        await watching.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(loss.ContinueWith(_ => { }, TaskScheduler.Default), polling).ConfigureAwait(false);
        return lost;
    }

    private async Task PollAsync(Connected connected, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(timing.StatusInterval, cancellationToken).ConfigureAwait(false);
                await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ReadStatusAsync(connected).ConfigureAwait(false);
                }
                catch (GpSockRefusedException)
                {
                    // The camera is there and said no; the next poll asks again.
                }
                catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    connected.Lose(Problem.WhileTalking(failure));
                    return;
                }
                finally
                {
                    operations.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The connection ended.
        }
    }

    /// <summary>Runs one operation the person asked for, after the ones before it, with <paramref name="task"/> shown meanwhile.</summary>
    private Task? Perform(CameraTask task, Func<Connected, Task> operation)
    {
        Task? run = null;
        CameraState? busy = null;
        lock (gate)
        {
            if (current is { } connected)
            {
                if (state.Task is not null)
                {
                    // Something is already in progress; the window disables what would clash with it.
                    return null;
                }

                // Marked busy, created and tracked under the lock that ends a connection: a second press
                // cannot slip in before the first is shown, and a connection ending now still waits for
                // this operation before it lets go of what the operation uses.
                state = busy = state with { Task = task };
                var created = new Task<Task>(() => RunAsync(connected, operation));
                run = connected.Track(created.Unwrap());
                created.Start(TaskScheduler.Default);
            }
        }

        if (busy is null)
        {
            PostNotice(State.Connection is Connection.Reconnecting
                ? "The camera is reconnecting. Try again in a moment."
                : "Connect to the camera first.");
            return null;
        }

        StateChanged?.Invoke(busy);
        return run;
    }

    private async Task RunAsync(Connected connected, Func<Connected, Task> operation)
    {
        try
        {
            await operations.WaitAsync(connected.Work.Token).ConfigureAwait(false);
            try
            {
                await operation(connected).ConfigureAwait(false);
            }
            finally
            {
                operations.Release();
            }
        }
        catch (GpSockRefusedException refused)
        {
            PostNotice($"The camera said no: {GpSockRefusedException.Explain(refused.Reason)}.");
        }
        catch (RefusalException refused)
        {
            PostNotice(refused.Message);
        }
        catch (Exception) when (connected.Work.IsCancellationRequested)
        {
            // The connection ended under it. What a request cut off mid-way throws depends on how far the
            // channel had closed: a cancellation, or a disposed channel. Neither says anything about the camera.
        }
        catch (Exception failure)
        {
            connected.Lose(Problem.WhileTalking(failure));
        }
        finally
        {
            Update(s => s with { Task = null });
        }
    }

    private void RefuseWhileRecording(string message)
    {
        if (State.IsRecording)
        {
            throw new RefusalException(message);
        }
    }

    private async Task EnterModeAsync(Connected connected, CameraMode mode)
    {
        if (cameraMode != mode)
        {
            await AskAsync(connected, (c, t) => c.SetModeAsync(mode, t)).ConfigureAwait(false);
            await ReadStatusAsync(connected).ConfigureAwait(false);
        }
    }

    private async Task<CameraStatus> ReadStatusAsync(Connected connected)
    {
        var status = CameraStatus.Of(await AskAsync(connected, (c, t) => c.GetStatusAsync(t)).ConfigureAwait(false));
        cameraMode = status.Mode;
        Update(s => s with { Status = status });
        return status;
    }

    private async Task BrowseAsync(Connected connected, Func<Task> block)
    {
        lock (gate)
        {
            browsing = true;
        }

        await StopLiveAsync().ConfigureAwait(false);
        try
        {
            await AskAsync(connected, (c, t) => c.SetModeAsync(CameraMode.Browse, t)).ConfigureAwait(false);
            await block().ConfigureAwait(false);
        }
        finally
        {
            // Best effort and bounded: a camera that is gone is found by the next poll.
            try
            {
                using var restore = new CancellationTokenSource(timing.Answer);
                await connected.Session.Control.SetModeAsync(State.Mode.CameraMode(), restore.Token).ConfigureAwait(false);
                var status = CameraStatus.Of(await connected.Session.Control.GetStatusAsync(restore.Token).ConfigureAwait(false));
                cameraMode = status.Mode;
                Update(s => s with { Status = status });
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Nothing more to do here.
            }

            lock (gate)
            {
                browsing = false;
            }

            // The camera hung up its stream to browse, and will not answer another until it restarts.
            Update(s => s with { HoldsLivePicture = false });
            UpdateLive();
        }
    }

    private async Task FetchThumbnailAsync(Connected connected, CameraFile file)
    {
        byte[] jpeg;
        try
        {
            jpeg = await AskAsync(connected, (c, t) => c.GetThumbnailAsync(file.Index, t)).ConfigureAwait(false);
        }
        catch (GpSockRefusedException)
        {
            // A file the camera has no thumbnail for is shown by its kind instead.
            return;
        }

        Update(s => s with { Library = s.Library with { Thumbnails = s.Library.Thumbnails.SetItem(file, jpeg) } });
    }

    private async Task CopyAsync(Connected connected, CameraFile file, IMediaSink sink)
    {
        IPendingMedia? pending = null;
        var output = new SniffingStream(kind =>
        {
            pending = sink.Create(file, kind);
            return pending.Output;
        });
        UpdateTransfers(new() { [file] = new Transfer.Copying(0, file.SizeKilobytes * 1024) });
        try
        {
            await WithStallDetectorAsync(connected, async (progressed, token) =>
            {
                var progress = new Progress(bytes =>
                {
                    progressed();
                    UpdateTransfers(new() { [file] = new Transfer.Copying(bytes, file.SizeKilobytes * 1024) });
                });
                await connected.Session.Control.DownloadAsync(file.Index, output, progress, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            output.Finish();
            // Nothing is created until the first bytes arrive, so no destination means no bytes.
            var saved = pending ?? throw new RefusalException("The camera sent nothing for this file.");
            string where;
            try
            {
                await saved.Output.DisposeAsync().ConfigureAwait(false);
                where = saved.Publish();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw new SaveFailureException(failure.Message, failure);
            }

            UpdateTransfers(new() { [file] = new Transfer.Saved(where) });
        }
        catch (Exception failure)
        {
            try
            {
                pending?.Discard();
            }
            catch (Exception discarding) when (discarding is IOException or UnauthorizedAccessException)
            {
                // Tidying up; a failure here must not hide why the copy failed.
            }

            var reason = failure switch
            {
                GpSockRefusedException refused => $"The camera said no: {GpSockRefusedException.Explain(refused.Reason)}.",
                RefusalException refused => refused.Message,
                SaveFailureException saving => $"This PC could not save it: {saving.Message}",
                _ => null,
            };
            if (reason is null)
            {
                // Cancelled, or the camera stopped answering: the rest cannot be copied either.
                throw;
            }

            UpdateTransfers(new() { [file] = new Transfer.Failed(reason) });
        }
    }

    private async Task DeleteCopiedAsync(Connected connected, IReadOnlyList<CameraFile> files)
    {
        var saved = files.Where(f => State.Library.Transfers.GetValueOrDefault(f) is Transfer.Saved).ToList();
        if (saved.Count == 0)
        {
            return;
        }

        foreach (var file in saved.OrderByDescending(f => f.Index))
        {
            await AskAsync(connected, (c, t) => c.DeleteFileAsync(file.Index, t)).ConfigureAwait(false);
        }

        SetFiles((await AskAsync(connected, (c, t) => c.GetFileListAsync(t), timing.LongAnswer).ConfigureAwait(false)).ToImmutableList());
    }

    /// <summary>Runs <paramref name="transfer"/>, failing it if it goes <see cref="ControllerTiming.TransferStall"/> without progress.</summary>
    private async Task WithStallDetectorAsync(Connected connected, Func<Action, CancellationToken, Task> transfer)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(connected.Work.Token);
        var clock = Stopwatch.StartNew();
        var watching = Task.Run(async () =>
        {
            while (!stalled.IsCancellationRequested)
            {
                await Task.Delay(timing.TransferStall / 4, stalled.Token).ConfigureAwait(false);
                if (clock.Elapsed >= timing.TransferStall)
                {
                    await stalled.CancelAsync().ConfigureAwait(false);
                }
            }
        });
        try
        {
            await transfer(() => clock.Restart(), stalled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stalled.IsCancellationRequested && !connected.Work.IsCancellationRequested)
        {
            throw new TimeoutException($"The camera stopped sending for {timing.TransferStall.TotalSeconds:0} seconds.");
        }
        finally
        {
            await stalled.CancelAsync().ConfigureAwait(false);
            await watching.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    private void UpdateLive()
    {
        LiveView? shown;
        lock (gate)
        {
            var connected = current;
            var asked = pictureAsked || askingHolders.Count > 0;
            var wanted = connected is not null && liveHolders.Count > 0 && !browsing && asked;
            if (wanted && live is null)
            {
                live = new CancellationTokenSource();
                var token = live.Token;
                liveTask = Task.Run(() => RunLiveAsync(connected!, token), CancellationToken.None);
            }
            else if (!wanted && live is not null)
            {
                live.Cancel();
                live = null;
            }

            shown = connected is null || liveHolders.Count == 0 ? LiveView.Off.Instance
                : browsing ? LiveView.Paused.Instance
                : !asked ? LiveView.Offered.Instance
                : null;
        }

        if (shown is not null)
        {
            Update(s => s with { Live = shown });
        }
    }

    private async Task StopLiveAsync()
    {
        CancellationTokenSource? cancel;
        Task? running;
        lock (gate)
        {
            cancel = live;
            running = liveTask;
            live = null;
            liveTask = null;
        }

        if (cancel is not null)
        {
            await cancel.CancelAsync().ConfigureAwait(false);
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }

        cancel?.Dispose();
        UpdateLive();
    }

    /// <summary>
    /// Shows the camera's pictures until cancelled. A quiet spell is said and waited out on the same stream;
    /// a picture the camera will not give again ends it.
    /// </summary>
    private async Task RunLiveAsync(Connected connected, CancellationToken cancellationToken)
    {
        try
        {
            ShowLive(LiveView.Starting.Instance, cancellationToken);

            // Until cancelled, which reading the stream ends by throwing.
            while (true)
            {
                Stopwatch? window = null;
                var windowFrames = 0;
                try
                {
                    await foreach (var frame in connected.Session.StreamFramesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        FrameArrived?.Invoke(new LiveFrame(frame.Jpeg, frame.Width, frame.Height, Interlocked.Increment(ref frameNumber)));
                        if (window is null)
                        {
                            // The first picture starts the count: how long the stream took to start is not its rate.
                            window = Stopwatch.StartNew();
                            lock (gate)
                            {
                                pictureRan = true;
                            }

                            ShowLive(new LiveView.Playing(0), cancellationToken, holds: true);
                        }
                        else
                        {
                            windowFrames++;
                            if (window.Elapsed >= TimeSpan.FromSeconds(1))
                            {
                                var perSecond = windowFrames / window.Elapsed.TotalSeconds;
                                ShowLive(new LiveView.Playing(perSecond), cancellationToken);
                                window.Restart();
                                windowFrames = 0;
                            }
                        }
                    }
                }
                catch (TimeoutException quiet)
                {
                    // The stream is still open; watching it again costs nothing.
                    ShowLive(new LiveView.Interrupted(quiet.Message), cancellationToken);
                }
                catch (LivePictureUnavailableException unavailable)
                {
                    // Asking again would open a connection the camera never answers, so this is where it stops.
                    ShowLive(new LiveView.Unavailable(unavailable.Message), cancellationToken, holds: false);
                    return;
                }

                // The pictures end quietly only when this run is stopped, and this is then the way out; after a
                // quiet spell it lets the stream be watched again.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped on purpose.
        }
    }

    /// <summary>
    /// Shows <paramref name="view"/> as the live view's state, unless the run it belongs to was stopped.
    /// </summary>
    /// <remarks>
    /// A run is stopped under the same lock this checks under, so a run that is stopping can never write
    /// Starting or Interrupted over the Off or Paused that stopping it showed.
    /// </remarks>
    private void ShowLive(LiveView view, CancellationToken run, bool? holds = null) =>
        Update(s => run.IsCancellationRequested ? s : s with { Live = view, HoldsLivePicture = holds ?? s.HoldsLivePicture });

    private Task<T> AskAsync<T>(Connected connected, Func<GpSockConnection, CancellationToken, Task<T>> request, TimeSpan? limit = null) =>
        AskCoreAsync(connected, request, limit ?? timing.Answer);

    private async Task AskAsync(Connected connected, Func<GpSockConnection, CancellationToken, Task> request) =>
        await AskCoreAsync(connected, async (c, t) =>
        {
            await request(c, t).ConfigureAwait(false);
            return true;
        }, timing.Answer).ConfigureAwait(false);

    private static async Task<T> AskCoreAsync<T>(Connected connected, Func<GpSockConnection, CancellationToken, Task<T>> request, TimeSpan limit)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connected.Work.Token);
        deadline.CancelAfter(limit);
        try
        {
            return await request(connected.Session.Control, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !connected.Work.IsCancellationRequested)
        {
            throw new TimeoutException($"The camera did not answer within {limit.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>The choice setting <paramref name="id"/> is set to, or null when the camera will not say.</summary>
    private async Task<int?> ReadChoiceAsync(Connected connected, int id)
    {
        try
        {
            return await AskAsync(connected, (c, t) => c.GetChoiceAsync(id, t)).ConfigureAwait(false);
        }
        catch (GpSockRefusedException)
        {
            return null;
        }
    }

    /// <summary>The text of setting <paramref name="id"/>, or null when it is empty or the camera will not say.</summary>
    private async Task<string?> ReadTextAsync(Connected connected, int id)
    {
        try
        {
            var text = await AskAsync(connected, (c, t) => c.GetTextAsync(id, t)).ConfigureAwait(false);
            return text.Length > 0 ? text : null;
        }
        catch (GpSockRefusedException)
        {
            return null;
        }
    }

    private void SetFiles(ImmutableList<CameraFile> files) => Update(s => s with
    {
        Library = s.Library with
        {
            Files = files,
            Thumbnails = s.Library.Thumbnails.Where(t => files.Contains(t.Key)).ToImmutableDictionary(),
        },
    });

    private void UpdateTransfers(Dictionary<CameraFile, Transfer> changes) =>
        Update(s => s with { Library = s.Library with { Transfers = s.Library.Transfers.SetItems(changes) } });

    private void PostNotice(string text)
    {
        var id = Interlocked.Increment(ref noticeNumber);
        Update(s => s with { Notice = new Notice(id, text) });
    }

    private void Update(Func<CameraState, CameraState> change)
    {
        CameraState next;
        lock (gate)
        {
            next = change(state);
            if (next == state)
            {
                return;
            }

            state = next;
        }

        StateChanged?.Invoke(next);
    }

    /// <summary>The camera while connected: its session, its network, its operations, and the one signal that it was lost.</summary>
    private sealed class Connected(CameraSession session, ICameraLease lease, CancellationTokenSource work)
    {
        private readonly TaskCompletionSource<Problem> lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> running = [];

        public CameraSession Session => session;

        public ICameraLease Lease => lease;

        public CancellationTokenSource Work => work;

        public Task<Problem> LostTask => lost.Task;

        /// <summary>Ends the connection for <paramref name="problem"/>; the first reported is kept.</summary>
        public void Lose(Problem problem) => lost.TrySetResult(problem);

        public Task Track(Task operation)
        {
            lock (running)
            {
                running.Add(operation);
            }

            return operation;
        }

        public Task WaitForOperationsAsync()
        {
            lock (running)
            {
                return Task.WhenAll(running);
            }
        }
    }

    /// <summary>A piece of background work and the means to stop it.</summary>
    private sealed record Run(CancellationTokenSource Cancel, Task Task);

    private sealed class Progress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>The app declining something on the camera's behalf, with the reason to show.</summary>
    private sealed class RefusalException(string message) : Exception(message);
}
