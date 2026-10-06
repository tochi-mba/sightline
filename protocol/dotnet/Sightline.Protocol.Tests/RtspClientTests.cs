using System.Text;
using Shouldly;
using Sightline.Protocol.Rtp;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>
/// The RTSP client, against replies shaped like the reference camera's.
/// </summary>
/// <remarks>
/// The SDP here is the one the camera really sent on 2026-10-02, including the detail that makes
/// the naive reading wrong: <c>track0</c> is the video and <c>track1</c> is the audio, so taking
/// the last <c>a=control</c> line selects sound and no picture ever arrives.
/// </remarks>
public sealed class RtspClientTests
{
    private const string ReferenceSdp =
        "v=0\r\n"
        + "o=- 1 1 IN IP4 127.0.0.1\r\n"
        + "s=Test\r\n"
        + "a=type:broadcast\r\n"
        + "t=0 0\r\n"
        + "c=IN IP4 0.0.0.0\r\n"
        + "m=video 0 RTP/AVP 26\r\n"
        + "a=control:track0\r\n"
        + "m=audio 0 RTP/AVP 97\r\n"
        + "a=rtpmap:97 L16/16000/1\r\n"
        + "a=control:track1\r\n";

    /// <summary>The Transport header the reference camera answered SETUP with on 2026-10-06.</summary>
    private const string ReferenceTransport = "RTP/AVP;unicast;client_port=63721-63722;server_port=59728-59729";

    [Fact]
    public async Task The_video_track_is_the_stream_url_with_track0_after_the_query_string()
    {
        var client = new RtspClient(new ScriptedTransport([]), "192.168.100.1");

        client.VideoTrackUrl.ShouldBe("rtsp://192.168.100.1:8080/?action=stream/track0");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_describe_reply_body_is_read_rather_than_left_in_the_socket()
    {
        // Leaving the SDP unread is what desynchronises every later reply; the next request then
        // appears to be answered with "v=0".
        var transport = new ScriptedTransport([
            Reply(200, ReferenceSdp),
            Reply(200, "", ("Session", "DEDEDEDEDEDEDEDEDEDEDEDEDEDEDE"), ("Transport", ReferenceTransport)),
        ]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        var describe = await client.DescribeAsync(CancellationToken.None);
        describe.Body.ShouldContain("m=video 0 RTP/AVP 26");

        var setup = await client.SetupVideoAsync(63721, CancellationToken.None);
        setup.IsSuccess.ShouldBeTrue();
        client.Session.ShouldBe("DEDEDEDEDEDEDEDEDEDEDEDEDEDEDE");
    }

    [Fact]
    public async Task The_session_is_taken_from_setup_and_sent_on_later_requests()
    {
        var transport = new ScriptedTransport([
            Reply(200, "", ("Session", "222222222222222222222222222222")),
            Reply(200, ""),
        ]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.SetupVideoAsync(50100, CancellationToken.None);
        await client.PlayAsync(CancellationToken.None);

        transport.Sent.ShouldContain(text => text.Contains("Session: 222222222222222222222222222222", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_setup_asks_for_the_stream_as_datagrams_to_the_port_given()
    {
        // Over this connection instead, the camera streams once per power-on and then leaves its buttons stuck.
        var transport = new ScriptedTransport([Reply(200, "")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.SetupVideoAsync(63721, CancellationToken.None);

        transport.Sent[0].ShouldStartWith("SETUP rtsp://192.168.100.1:8080/?action=stream/track0 RTSP/1.0");
        transport.Sent[0].ShouldContain("Transport: RTP/AVP;unicast;client_port=63721-63722\r\n");
    }

    [Fact]
    public async Task The_port_the_camera_streams_from_is_read_from_its_setup_reply()
    {
        var transport = new ScriptedTransport([Reply(200, "", ("Transport", ReferenceTransport), ("Session", "F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0"))]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.SetupVideoAsync(63721, CancellationToken.None);

        client.ServerPort.ShouldBe(59728);
        client.Session.ShouldBe("F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RTP/AVP;unicast;client_port=63721-63722")]
    [InlineData("RTP/AVP;unicast;server_port")]
    [InlineData("RTP/AVP;unicast;server_port=many")]
    [InlineData("RTP/AVP;unicast;server_port=0-1")]
    [InlineData("RTP/AVP;unicast;server_port=70000-70001")]
    [InlineData("RTP/AVP;unicast;server_port=-5")]
    public async Task A_setup_reply_with_no_usable_server_port_leaves_it_unknown(string? header)
    {
        var headers = header is null ? [] : new[] { ("Transport", header) };
        var transport = new ScriptedTransport([Reply(200, "", headers)]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        (await client.SetupVideoAsync(50100, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        client.ServerPort.ShouldBeNull();
        client.Session.ShouldBeNull();
    }

    [Fact]
    public async Task The_server_port_is_found_whatever_its_case_and_wherever_it_comes()
    {
        var transport = new ScriptedTransport([Reply(200, "", ("Transport", "RTP/AVP;unicast; Server_Port=6970;client_port=50100-50101"))]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.SetupVideoAsync(50100, CancellationToken.None);

        client.ServerPort.ShouldBe(6970);
    }

    [Fact]
    public async Task A_refusal_is_reported_with_its_status_rather_than_thrown_away()
    {
        var transport = new ScriptedTransport([Reply(404, "")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        var reply = await client.SetupVideoAsync(50100, CancellationToken.None);

        reply.IsSuccess.ShouldBeFalse();
        reply.StatusCode.ShouldBe(404);
        client.Session.ShouldBeNull();
        client.ServerPort.ShouldBeNull();
    }

    [Fact]
    public async Task Waiting_for_the_close_ends_when_the_camera_closes_the_connection_whatever_it_sent_first()
    {
        // Browsing the card is the camera closing the stream's connection. Anything it sends before that is not
        // the stream, which comes as datagrams.
        var play = Reply(200, "");
        var transport = new ScriptedTransport([[.. play, 1, 2, 3], [4, 5, 6]]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);
        await client.PlayAsync(CancellationToken.None);
        await transport.SendAsync(Array.Empty<byte>(), CancellationToken.None);

        await client.WaitForCloseAsync(CancellationToken.None);

        transport.Reads.ShouldBe(3);
    }

    [Fact]
    public async Task Waiting_for_the_close_can_be_given_up()
    {
        var transport = new ScriptedTransport([]) { BlocksWhenEmpty = true };
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => client.WaitForCloseAsync(cancel.Token));
    }

    [Fact]
    public async Task A_reply_split_across_reads_is_assembled()
    {
        var transport = new ScriptedTransport([Reply(200, ReferenceSdp)]) { DribbleBytes = 5 };
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        var reply = await client.DescribeAsync(CancellationToken.None);

        reply.IsSuccess.ShouldBeTrue();
        reply.Body.ShouldContain("a=control:track0");
    }

    [Fact]
    public async Task A_camera_that_hangs_up_mid_reply_is_reported()
    {
        var transport = new ScriptedTransport([Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\n")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await Should.ThrowAsync<RtspException>(() => client.DescribeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_reply_that_is_not_rtsp_is_reported_rather_than_guessed_at()
    {
        var transport = new ScriptedTransport([Encoding.ASCII.GetBytes("hello there\r\n\r\n")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await Should.ThrowAsync<RtspException>(() => client.OptionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Play_names_the_session_setup_was_given()
    {
        var transport = new ScriptedTransport([Reply(200, "", ("Session", "6363636363636363636363636363")), Reply(200, "")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);
        await client.SetupVideoAsync(50100, CancellationToken.None);

        var reply = await client.PlayAsync(CancellationToken.None);

        reply.IsSuccess.ShouldBeTrue();
        transport.Sent[^1].ShouldStartWith("PLAY rtsp://192.168.100.1:8080/?action=stream RTSP/1.0");
        transport.Sent[^1].ShouldContain("Session: 6363636363636363636363636363");
    }

    [Theory]
    [InlineData("\r\n\r\n")]
    [InlineData("RTSP/1.0\r\n\r\n")]
    [InlineData("RTSP/1.0 OK fine\r\n\r\n")]
    public async Task A_reply_with_no_readable_status_is_reported(string reply)
    {
        var client = new RtspClient(new ScriptedTransport([Encoding.ASCII.GetBytes(reply)]), "192.168.100.1");

        await Should.ThrowAsync<RtspException>(() => client.OptionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_header_line_with_no_colon_is_ignored_rather_than_fatal()
    {
        var reply = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\nnonsense\r\nPublic: DESCRIBE\r\n\r\n");
        var client = new RtspClient(new ScriptedTransport([reply]), "192.168.100.1");

        var options = await client.OptionsAsync(CancellationToken.None);

        options.Header("Public").ShouldBe("DESCRIBE");
        options.Header("nonsense").ShouldBeNull();
    }

    [Fact]
    public async Task A_body_cut_short_by_the_camera_hanging_up_is_returned_as_far_as_it_got()
    {
        var reply = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nContent-Length: 100\r\n\r\nv=0\r\n");
        var client = new RtspClient(new ScriptedTransport([reply]), "192.168.100.1");

        var describe = await client.DescribeAsync(CancellationToken.None);

        describe.Body.ShouldBe("v=0\r\n");
    }

    [Fact]
    public async Task A_content_length_that_is_not_a_number_means_no_body()
    {
        var reply = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nContent-Length: lots\r\n\r\n");
        var client = new RtspClient(new ScriptedTransport([reply]), "192.168.100.1");

        (await client.DescribeAsync(CancellationToken.None)).Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disposing_the_client_closes_its_connection()
    {
        var transport = new ScriptedTransport([]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.DisposeAsync();

        transport.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void A_client_needs_a_transport_and_a_host()
    {
        Should.Throw<ArgumentNullException>(() => new RtspClient(null!, "192.168.100.1"));
        Should.Throw<ArgumentException>(() => new RtspClient(new ScriptedTransport([]), " "));
    }

    [Fact]
    public void The_exception_carries_its_message_and_cause()
    {
        var cause = new IOException("cause");

        new RtspException().Message.ShouldNotBeNullOrWhiteSpace();
        new RtspException("broken", cause).InnerException.ShouldBe(cause);
    }

    private static byte[] Reply(int status, string body, params (string Name, string Value)[] headers)
    {
        var text = new StringBuilder($"RTSP/1.0 {status} OK\r\nCSeq: 1\r\n");
        foreach (var (name, value) in headers)
        {
            text.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (body.Length > 0)
        {
            text.Append("Content-Type: application/sdp\r\n")
                .Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        }

        text.Append("\r\n").Append(body);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>A transport that hands back prepared replies, one per request.</summary>
    private sealed class ScriptedTransport(IReadOnlyList<byte[]> replies) : ICameraTransport
    {
        private readonly Queue<byte[]> queued = new();
        private int next;

        public List<string> Sent { get; } = [];

        public int DribbleBytes { get; init; }

        public bool BlocksWhenEmpty { get; init; }

        public int Reads { get; private set; }

        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.ASCII.GetString(bytes.Span));
            if (next < replies.Count)
            {
                queued.Enqueue(replies[next++]);
            }

            return Task.CompletedTask;
        }

        public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
        {
            Reads++;
            if (queued.Count == 0)
            {
                if (BlocksWhenEmpty)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return 0;
            }

            var reply = queued.Dequeue();
            var take = DribbleBytes > 0 ? Math.Min(DribbleBytes, reply.Length) : reply.Length;
            take = Math.Min(take, into.Length);
            reply.AsSpan(0, take).CopyTo(into.Span);
            if (take < reply.Length)
            {
                var remainder = reply[take..];
                var rest = queued.ToArray();
                queued.Clear();
                queued.Enqueue(remainder);
                foreach (var item in rest)
                {
                    queued.Enqueue(item);
                }
            }

            return take;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
