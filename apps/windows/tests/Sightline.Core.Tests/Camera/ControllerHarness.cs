using Sightline.Core.Camera;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Testing;

namespace Sightline.Core.Tests.Camera;

/// <summary>
/// A controller driving the shared fake camera over a fake network, on short real timings. The camera
/// describes itself with the reference camera's own menu, and each live view gets a fresh fake stream.
/// </summary>
internal sealed class ControllerHarness : IAsyncDisposable
{
    /// <summary>The controller's timings, shortened so a test of a stall or a reconnect takes milliseconds.</summary>
    public static readonly ControllerTiming Quick = new(
        Answer: TimeSpan.FromMilliseconds(400),
        LongAnswer: TimeSpan.FromSeconds(3),
        TransferStall: TimeSpan.FromMilliseconds(400),
        StatusInterval: TimeSpan.FromMilliseconds(100),
        LiveRetry: TimeSpan.FromMilliseconds(50),
        LiveRetryCap: TimeSpan.FromMilliseconds(150),
        ReconnectDelay: TimeSpan.FromMilliseconds(50),
        ReconnectAttempts: 3);

    /// <summary>The stream's timings, shortened likewise.</summary>
    public static readonly CameraSessionTiming QuickStream = new(
        TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200));

    public ControllerHarness(bool reconnects = true)
    {
        Link = new FakeLink(this);
        Controller = new CameraController(Link, Quick, QuickStream, () => reconnects);
        Controller.StateChanged += state =>
        {
            lock (Seen)
            {
                Seen.Add(state);
            }
        };
    }

    public FakeCamera Control { get; } = new() { MenuXml = File.ReadAllText(Path.Combine("golden", "menu", "reference-camera.xml")) };

    public List<FakeRtspCamera> Streams { get; } = [];

    /// <summary>How each new stream is set up before the controller opens it.</summary>
    public Action<FakeRtspCamera> Stream { get; set; } = s => s.Frames.Add(FakeRtspCamera.Jpeg(600));

    public FakeLink Link { get; }

    public CameraController Controller { get; }

    /// <summary>Every state the controller announced, in order.</summary>
    public List<CameraState> Seen { get; } = [];

    public CameraState State => Controller.State;

    /// <summary>Waits for the controller to reach a state <paramref name="predicate"/> accepts.</summary>
    public async Task<CameraState> UntilAsync(Func<CameraState, bool> predicate, int seconds = 10)
    {
        var reached = new TaskCompletionSource<CameraState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(CameraState state)
        {
            if (predicate(state))
            {
                reached.TrySetResult(state);
            }
        }

        Controller.StateChanged += Watch;
        try
        {
            Watch(Controller.State);
            return await reached.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            Controller.StateChanged -= Watch;
        }
    }

    /// <summary>
    /// Waits for <paramref name="condition"/>, about something other than the controller's state such as
    /// what the fake camera was sent, by looking again every few milliseconds.
    /// </summary>
    public static async Task EventuallyAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Connects and waits until connected.</summary>
    public async Task<CameraState> ConnectedAsync()
    {
        _ = Controller.Connect();
        return await UntilAsync(s => s.IsConnected);
    }

    public async ValueTask DisposeAsync() => await Controller.DisposeAsync();

    /// <summary>The camera's network, which joins unless told to fail.</summary>
    internal sealed class FakeLink(ControllerHarness harness) : ICameraLink
    {
        public List<bool> Requests { get; } = [];

        public Queue<Exception> Failures { get; } = new();

        public List<FakeLease> Leases { get; } = [];

        /// <summary>When set, a join waits until it is given up.</summary>
        public bool Waits { get; set; }

        /// <summary>When set, the control port is this rather than the fake camera.</summary>
        public Func<ICameraTransport>? ControlTransport { get; set; }

        public FakeLease Lease => Leases[^1];

        public async Task<ICameraLease> JoinAsync(bool reconnecting, CancellationToken cancellationToken)
        {
            Requests.Add(reconnecting);
            if (Waits)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            var lease = new FakeLease(harness);
            Leases.Add(lease);
            return lease;
        }
    }

    internal sealed class FakeLease(ControllerHarness harness) : ICameraLease
    {
        private readonly TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public Task Lost => lost.Task;

        /// <summary>Has the system lose the camera's network.</summary>
        public void Lose() => lost.TrySetResult();

        public ICameraTransport Transport(int port)
        {
            if (port == GpSockConnection.Port)
            {
                return harness.Link.ControlTransport?.Invoke() ?? harness.Control;
            }

            var stream = new FakeRtspCamera { StreamStarted = () => harness.Control.IsStreaming };
            harness.Stream(stream);
            lock (harness.Streams)
            {
                harness.Streams.Add(stream);
            }

            return stream;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A gallery in memory that can be told to fail.</summary>
internal sealed class FakeSink : IMediaSink
{
    public List<(CameraFile File, MediaKind Kind)> Created { get; } = [];

    public Dictionary<string, byte[]> Published { get; } = [];

    public List<CameraFile> Discarded { get; } = [];

    /// <summary>When set, creating a file fails as a full disk does.</summary>
    public bool Full { get; set; }

    /// <summary>When set, the last step, giving the file its name, fails.</summary>
    public bool PublishFails { get; set; }

    /// <summary>When set, Windows denies the last step, as it does a folder the person may not write to.</summary>
    public bool PublishDenied { get; set; }

    /// <summary>When set, throwing a partial file away fails too.</summary>
    public bool DiscardFails { get; set; }

    /// <summary>Runs as each file is created: the moment a copy is known to be under way.</summary>
    public Action OnCreate { get; set; } = () => { };

    public IPendingMedia Create(CameraFile file, MediaKind kind)
    {
        if (Full)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        OnCreate();
        Created.Add((file, kind));
        return new Pending(this, file, kind);
    }

    private sealed class Pending(FakeSink sink, CameraFile file, MediaKind kind) : IPendingMedia, IDisposable
    {
        private readonly MemoryStream bytes = new();

        public Stream Output => bytes;

        public string Publish()
        {
            if (sink.PublishFails)
            {
                throw new IOException("The file is in use by another process.");
            }

            if (sink.PublishDenied)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }

            var name = $"Downloads/{file.DisplayName}{kind.Extension()}";
            sink.Published[name] = bytes.ToArray();
            return name;
        }

        public void Discard()
        {
            sink.Discarded.Add(file);
            if (sink.DiscardFails)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }

        public void Dispose() => bytes.Dispose();
    }
}
