using System.Net;
using System.Net.Sockets;

namespace Sightline.Protocol;

/// <summary>
/// A byte pipe to the camera.
/// </summary>
/// <remarks>
/// Every socket in this project goes through this seam for two reasons. Tests get a camera that
/// needs no hardware, and the real implementation is the single place that must bind to the
/// adapter the camera is on — which on a phone or a laptop with another network is the difference
/// between reaching the camera and quietly leaving over the wrong interface.
/// </remarks>
public interface ICameraTransport : IAsyncDisposable
{
    /// <summary>Whether the pipe is open.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the pipe.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Sends every byte of <paramref name="bytes"/>.</summary>
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Reads what has arrived, returning 0 when the camera closed the pipe.</summary>
    Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken);
}

/// <summary>A TCP pipe to the camera, optionally bound to one local address.</summary>
public sealed class TcpCameraTransport : ICameraTransport
{
    private readonly IPEndPoint endpoint;
    private readonly IPAddress? bindTo;
    private Socket? socket;

    /// <summary>Creates a transport.</summary>
    /// <param name="endpoint">The camera's address and port.</param>
    /// <param name="bindTo">
    /// The local address to send from. Supplying it is what keeps the traffic on the adapter the
    /// camera is actually on when the machine has another network; leaving it null lets the
    /// routing table decide, which is right only when nothing else could be chosen.
    /// </param>
    public TcpCameraTransport(IPEndPoint endpoint, IPAddress? bindTo = null)
    {
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.bindTo = bindTo;
    }

    /// <summary>Where the camera is.</summary>
    public IPEndPoint Endpoint => endpoint;

    /// <summary>The local address the connection is sent from, or null when the routing table decides.</summary>
    public IPAddress? BoundTo => bindTo;

    /// <inheritdoc />
    public bool IsConnected => socket?.Connected ?? false;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var created = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (bindTo is not null)
            {
                created.Bind(new IPEndPoint(bindTo, 0));
            }

            // Nagle batches small writes, and every control command here is small and latency
            // sensitive: a shutter press should not wait for a second packet to keep it company.
            created.NoDelay = true;
            await created.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            created.Dispose();
            throw;
        }

        socket = created;
    }

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var open = socket ?? throw new InvalidOperationException("The transport is not connected.");
        var sent = 0;
        while (sent < bytes.Length)
        {
            sent += await open.SendAsync(bytes[sent..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        var open = socket ?? throw new InvalidOperationException("The transport is not connected.");
        return await open.ReceiveAsync(into, SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        socket?.Dispose();
        socket = null;
        return ValueTask.CompletedTask;
    }
}
