using Sightline.Core.Camera;
using Sightline.Core.Testing;
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
    public static readonly CameraSessionTiming QuickStream = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300));

    /// <summary>A transfer that stalls is given up only after a long while, for a test that stops one itself.</summary>
    public static readonly ControllerTiming Patient = Quick with { TransferStall = TimeSpan.FromSeconds(30) };

    public ControllerHarness(bool reconnects = true, ControllerTiming? timing = null)
    {
        Link = new FakeLink(ReferenceCamera.Fake());
        Controller = new CameraController(Link, timing ?? Quick, QuickStream, () => reconnects);
        Controller.StateChanged += state =>
        {
            lock (Seen)
            {
                Seen.Add(state);
            }
        };
    }

    public FakeCamera Control => Link.Control;

    public List<FakeRtspCamera> Streams => Link.Streams;

    /// <summary>How each new stream is set up before the controller opens it.</summary>
    public Action<FakeRtspCamera> Stream
    {
        get => Link.Stream;
        set => Link.Stream = value;
    }

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
}
