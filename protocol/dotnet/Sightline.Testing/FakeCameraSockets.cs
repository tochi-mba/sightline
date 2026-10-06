using System.Threading.Channels;
using Sightline.Protocol;

namespace Sightline.Testing;

/// <summary>
/// The camera's network, needing no hardware: connections from a function, and datagram sockets that the
/// fake RTSP camera finds by the port SETUP names, as the real camera finds this machine's.
/// </summary>
public sealed class FakeCameraSockets : ICameraSockets
{
    private readonly Func<int, ICameraTransport> transports;
    private readonly List<FakeCameraDatagrams> opened = [];
    private int nextPort = 50100;

    /// <summary>A network whose connections come from <paramref name="transports"/>, by port.</summary>
    public FakeCameraSockets(Func<int, ICameraTransport> transports)
    {
        this.transports = transports ?? throw new ArgumentNullException(nameof(transports));
    }

    /// <summary>Every datagram socket opened, in order.</summary>
    public IReadOnlyList<FakeCameraDatagrams> Opened
    {
        get
        {
            lock (opened)
            {
                return [.. opened];
            }
        }
    }

    /// <inheritdoc />
    public ICameraTransport Transport(int port)
    {
        var transport = transports(port);
        if (transport is FakeRtspCamera camera)
        {
            camera.Sockets = this;
        }

        return transport;
    }

    /// <inheritdoc />
    public ICameraDatagrams Datagrams()
    {
        lock (opened)
        {
            // Odd ports as well as even: the real camera takes whichever it is told.
            var socket = new FakeCameraDatagrams(nextPort++);
            opened.Add(socket);
            return socket;
        }
    }

    /// <summary>The open socket at <paramref name="port"/>, or null if nothing is listening there.</summary>
    public FakeCameraDatagrams? At(int port)
    {
        lock (opened)
        {
            return opened.LastOrDefault(socket => socket.Port == port && !socket.IsDisposed);
        }
    }
}

/// <summary>
/// One datagram socket on the fake network. The camera delivers into it; whoever opened it receives, each
/// datagram after the delay it was delivered with, as the real camera's dozen pictures a second arrive.
/// </summary>
public sealed class FakeCameraDatagrams(int port) : ICameraDatagrams
{
    private readonly Channel<(byte[] Datagram, TimeSpan Delay)> inbox = Channel.CreateUnbounded<(byte[], TimeSpan)>();
    private readonly Lock gate = new();
    private int inFlight;
    private TaskCompletionSource? idle;

    /// <inheritdoc />
    public int Port => port;

    /// <summary>What was sent from this socket, and to which of the camera's ports.</summary>
    public List<(int Port, byte[] Datagram)> Sent { get; } = [];

    /// <summary>Whether it has been closed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Completes once every datagram delivered so far has been received, or dropped.
    /// </summary>
    public Task Idle
    {
        get
        {
            lock (gate)
            {
                return inFlight == 0 ? Task.CompletedTask : (idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    /// <summary>Delivers one datagram, to be received <paramref name="delay"/> after the one before.</summary>
    public void Deliver(byte[] datagram, TimeSpan delay = default)
    {
        lock (gate)
        {
            if (IsDisposed)
            {
                return;
            }

            inFlight++;
        }

        inbox.Writer.TryWrite((datagram, delay));
    }

    /// <summary>Drops everything delivered and not yet received, as datagrams in flight are when a stream is cut.</summary>
    public void Drop()
    {
        while (inbox.Reader.TryRead(out _))
        {
            Taken();
        }
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> datagram, int port, CancellationToken cancellationToken)
    {
        Sent.Add((port, datagram.ToArray()));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        var (datagram, delay) = await inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Taken();
        }

        // At most the reader's buffer, as a real socket truncates a datagram too long for it.
        var take = Math.Min(datagram.Length, into.Length);
        datagram.AsSpan(0, take).CopyTo(into.Span);
        return take;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            IsDisposed = true;
        }

        Drop();
        return ValueTask.CompletedTask;
    }

    private void Taken()
    {
        TaskCompletionSource? settled = null;
        lock (gate)
        {
            if (--inFlight == 0)
            {
                settled = idle;
                idle = null;
            }
        }

        settled?.TrySetResult();
    }
}
