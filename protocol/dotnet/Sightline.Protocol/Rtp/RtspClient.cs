using System.Globalization;
using System.Text;

namespace Sightline.Protocol.Rtp;

/// <summary>What the camera said in reply to one RTSP request.</summary>
/// <param name="StatusCode">The numeric status, 200 when it worked.</param>
/// <param name="Headers">The response headers, keyed case-insensitively.</param>
/// <param name="Body">The body, which is the SDP for a DESCRIBE and empty otherwise.</param>
public sealed record RtspReply(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Whether the camera accepted the request.</summary>
    public bool IsSuccess => StatusCode == 200;

    /// <summary>A header, or null when the camera did not send it.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The camera's RTSP server, on port 8080.
/// </summary>
/// <remarks>
/// <para>
/// Three things about this server differ from what a standards-compliant client expects, and all
/// three cost a working picture if they are not handled.
/// </para>
/// <list type="number">
/// <item>
/// The video track is addressed as the stream URL with <c>/track0</c> appended <b>after</b> the
/// query string, giving <c>rtsp://host:8080/?action=stream/track0</c>. Reading the last
/// <c>a=control</c> line of the SDP instead selects the audio track, and the picture never arrives.
/// </item>
/// <item>
/// A DESCRIBE reply has a body. Reading only as far as the blank line leaves the SDP in the socket
/// and every later read is misaligned, which looks like the camera talking nonsense.
/// </item>
/// <item>
/// The stream must be asked for over UDP. Asked for over this connection, the camera sends bare RTP
/// with no interleaved framing, answers that only once each time it is switched on, and leaves its own
/// buttons stuck once the stream ends, until its battery comes out. Over UDP it does none of that.
/// </item>
/// </list>
/// <para>
/// The stream also stays silent until the control channel has been told to start it — see
/// <c>GpSockConnection.StartStreamingAsync</c>. A session that negotiates perfectly and delivers
/// nothing is almost always that.
/// </para>
/// </remarks>
public sealed class RtspClient : IAsyncDisposable
{
    /// <summary>The port the camera serves RTSP on.</summary>
    public const int Port = 8080;

    /// <summary>The path the camera publishes its stream at.</summary>
    public const string StreamPath = "/?action=stream";

    private readonly ICameraTransport transport;
    private readonly string baseUrl;
    private readonly byte[] buffer = new byte[16 * 1024];
    private readonly List<byte> pending = [];
    private int sequence;

    /// <summary>Creates a client.</summary>
    /// <param name="transport">A pipe to the camera's RTSP port.</param>
    /// <param name="host">The camera's address, used to build request URLs.</param>
    public RtspClient(ICameraTransport transport, string host)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        baseUrl = $"rtsp://{host}:{Port}{StreamPath}";
    }

    /// <summary>The session the camera gave us, once SETUP has succeeded.</summary>
    public string? Session { get; private set; }

    /// <summary>
    /// The port the camera sends the stream from, once SETUP has succeeded, or null if it did not say.
    /// </summary>
    /// <remarks>
    /// A datagram sent to it from the stream's own port is what lets the stream in through a firewall
    /// that drops what it did not ask for, as Windows does on a public network.
    /// </remarks>
    public int? ServerPort { get; private set; }

    /// <summary>The URL of the video track, which is the base URL plus the track name.</summary>
    public string VideoTrackUrl => baseUrl + "/track0";

    /// <summary>Opens the connection.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        transport.ConnectAsync(cancellationToken);

    /// <summary>Asks what the camera supports.</summary>
    public Task<RtspReply> OptionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync("OPTIONS", baseUrl, null, cancellationToken);

    /// <summary>Asks for the stream description, whose body is SDP.</summary>
    public Task<RtspReply> DescribeAsync(CancellationToken cancellationToken = default) =>
        SendAsync("DESCRIBE", baseUrl, new() { ["Accept"] = "application/sdp" }, cancellationToken);

    /// <summary>Sets up the video track, asking for the stream as datagrams to <paramref name="clientPort"/>.</summary>
    /// <param name="clientPort">The local port the datagrams are to arrive at.</param>
    /// <param name="cancellationToken">Gives up.</param>
    public async Task<RtspReply> SetupVideoAsync(int clientPort, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(
            "SETUP",
            VideoTrackUrl,
            new() { ["Transport"] = string.Create(CultureInfo.InvariantCulture, $"RTP/AVP;unicast;client_port={clientPort}-{clientPort + 1}") },
            cancellationToken).ConfigureAwait(false);

        if (reply.IsSuccess)
        {
            if (reply.Header("Session") is { } session)
            {
                Session = session.Split(';')[0].Trim();
            }

            ServerPort = ServerPortIn(reply.Header("Transport"));
        }

        return reply;
    }

    /// <summary>Starts the stream.</summary>
    public Task<RtspReply> PlayAsync(CancellationToken cancellationToken = default) =>
        SendAsync("PLAY", baseUrl, new() { ["Range"] = "npt=0.000-" }, cancellationToken);

    /// <summary>
    /// Waits for the camera to close this connection, which is how it ends a stream: browsing its card
    /// does, for one. Anything it sends meanwhile is not the stream, which comes as datagrams, and is
    /// let go of.
    /// </summary>
    public async Task WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        pending.Clear();
        while (await transport.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    /// <summary>The first port of the <c>server_port</c> in a SETUP reply's Transport header, if it has one.</summary>
    private static int? ServerPortIn(string? transport)
    {
        foreach (var part in (transport ?? "").Split(';'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Trim().Equals("server_port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair[1].Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535)
            {
                return port;
            }
        }

        return null;
    }

    private async Task<RtspReply> SendAsync(
        string verb, string url, Dictionary<string, string>? extra, CancellationToken cancellationToken)
    {
        sequence++;
        var request = new StringBuilder()
            .Append(verb).Append(' ').Append(url).Append(" RTSP/1.0\r\n")
            .Append("CSeq: ").Append(sequence.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("User-Agent: Sightline\r\n");
        if (Session is not null)
        {
            request.Append("Session: ").Append(Session).Append("\r\n");
        }

        foreach (var (name, value) in extra ?? [])
        {
            request.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        request.Append("\r\n");
        await transport.SendAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken)
            .ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RtspReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        // Headers first.
        int headerEnd;
        while ((headerEnd = IndexOfBlankLine()) < 0)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new RtspException("The camera closed the connection part-way through a reply.");
            }
        }

        var headerText = Encoding.ASCII.GetString(pending.ToArray(), 0, headerEnd);
        pending.RemoveRange(0, headerEnd + 4);

        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new RtspException("The camera sent an empty reply.");
        }

        var statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], CultureInfo.InvariantCulture, out var status))
        {
            throw new RtspException($"The camera's reply did not start with a status: '{lines[0]}'.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        // Then exactly as much body as the camera said there would be. Skipping this is what
        // desynchronises every later read.
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var declared))
        {
            _ = int.TryParse(declared, CultureInfo.InvariantCulture, out length);
        }

        while (pending.Count < length)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        var take = Math.Min(length, pending.Count);
        var body = Encoding.UTF8.GetString(pending.ToArray(), 0, take);
        pending.RemoveRange(0, take);
        return new RtspReply(status, headers, body);
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        var read = await transport.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        pending.AddRange(buffer.AsSpan(0, read));
        return true;
    }

    private int IndexOfBlankLine()
    {
        for (var i = 0; i + 3 < pending.Count; i++)
        {
            if (pending[i] == '\r' && pending[i + 1] == '\n'
                && pending[i + 2] == '\r' && pending[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

/// <summary>The camera's RTSP server did something this client cannot make sense of.</summary>
public sealed class RtspException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RtspException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RtspException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public RtspException(string message, Exception inner) : base(message, inner)
    {
    }
}
