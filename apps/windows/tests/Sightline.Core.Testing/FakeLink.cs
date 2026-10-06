using Sightline.Core.Camera;
using Sightline.Protocol;
using Sightline.Protocol.GpSock;
using Sightline.Testing;

namespace Sightline.Core.Testing;

/// <summary>The camera's network over a fake camera, which joins unless told to fail.</summary>
/// <remarks>
/// The camera behind it keeps the reference camera's rule for its picture: it answers the first stream
/// connection after it is switched on and no other, until <see cref="PowerCycle"/>; and browse mode
/// hangs up the one it is serving.
/// </remarks>
public sealed class FakeLink : ICameraLink
{
    private readonly FakeCamera control;
    private int served;

    /// <summary>A network onto <paramref name="control"/>.</summary>
    public FakeLink(FakeCamera control)
    {
        this.control = control;
        control.EnteredBrowse += () =>
        {
            lock (Streams)
            {
                foreach (var stream in Streams)
                {
                    stream.HangUp();
                }
            }
        };
    }

    /// <summary>The camera's control channel.</summary>
    public FakeCamera Control => control;

    /// <summary>Switches the camera off and on: its next stream connection is answered again.</summary>
    public void PowerCycle()
    {
        lock (Streams)
        {
            served = 0;
        }
    }

    /// <summary>Whether each join was a reconnect, in order.</summary>
    public List<bool> Requests { get; } = [];

    /// <summary>What the next joins throw, in order; joins after these succeed.</summary>
    public Queue<Exception> Failures { get; } = new();

    /// <summary>Every network handed out.</summary>
    public List<FakeLease> Leases { get; } = [];

    /// <summary>Every stream opened, in order.</summary>
    public List<FakeRtspCamera> Streams { get; } = [];

    /// <summary>How each new stream is set up before the controller opens it.</summary>
    public Action<FakeRtspCamera> Stream { get; set; } = s => s.Frames.Add(FakeRtspCamera.Jpeg(600));

    /// <summary>When set, a join waits until it is given up.</summary>
    public bool Waits { get; set; }

    /// <summary>When set, the control port is this rather than the fake camera.</summary>
    public Func<ICameraTransport>? ControlTransport { get; set; }

    /// <summary>The network handed out last.</summary>
    public FakeLease Lease => Leases[^1];

    /// <inheritdoc />
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

        var lease = new FakeLease(this);
        Leases.Add(lease);
        return lease;
    }

    /// <summary>Opens a new stream, set up as <see cref="Stream"/> says.</summary>
    internal FakeRtspCamera OpenStream()
    {
        var stream = new FakeRtspCamera { StreamStarted = () => control.IsStreaming };
        Stream(stream);
        lock (Streams)
        {
            if (served++ > 0)
            {
                // A connection after the first since power-on: accepted, and never answered.
                stream.NeverAnswers ??= "DESCRIBE";
            }

            Streams.Add(stream);
        }

        return stream;
    }
}

/// <summary>The camera's network while held, which the test can have the system lose.</summary>
public sealed class FakeLease(FakeLink link) : ICameraLease
{
    private readonly TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether it has been let go of.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public Task Lost => lost.Task;

    /// <summary>Has the system lose the camera's network.</summary>
    public void Lose() => lost.TrySetResult();

    /// <inheritdoc />
    public ICameraTransport Transport(int port) =>
        port == GpSockConnection.Port ? link.ControlTransport?.Invoke() ?? link.Control : link.OpenStream();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
