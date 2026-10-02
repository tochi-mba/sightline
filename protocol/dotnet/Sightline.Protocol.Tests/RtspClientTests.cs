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
            Reply(200, "", ("Session", "DEDEDEDEDEDEDEDEDEDEDEDEDEDEDE"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")),
        ]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        var describe = await client.DescribeAsync(CancellationToken.None);
        describe.Body.ShouldContain("m=video 0 RTP/AVP 26");

        var setup = await client.SetupVideoAsync(CancellationToken.None);
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

        await client.SetupVideoAsync(CancellationToken.None);
        await client.PlayAsync(CancellationToken.None);

        transport.Sent.ShouldContain(text => text.Contains("Session: 222222222222222222222222222222", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_setup_asks_for_the_stream_over_the_same_connection()
    {
        // Inbound UDP is what a firewall drops, so the TCP transport is requested first.
        var transport = new ScriptedTransport([Reply(200, "")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.SetupVideoAsync(CancellationToken.None);

        transport.Sent[0].ShouldContain("Transport: RTP/AVP/TCP");
        transport.Sent[0].ShouldContain("/?action=stream/track0");
    }

    [Fact]
    public async Task A_refusal_is_reported_with_its_status_rather_than_thrown_away()
    {
        var transport = new ScriptedTransport([Reply(404, "")]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        var reply = await client.SetupVideoAsync(CancellationToken.None);

        reply.IsSuccess.ShouldBeFalse();
        reply.StatusCode.ShouldBe(404);
        client.Session.ShouldBeNull();
    }

    [Fact]
    public async Task Stream_bytes_that_arrived_with_the_play_reply_are_not_lost()
    {
        // The camera often packs the first RTP bytes into the same read as the PLAY reply. Dropping
        // them loses the start of the first picture.
        var play = Reply(200, "");
        var withStream = play.Concat(new byte[] { 0x80, 0x1A, 0x00, 0x01 }).ToArray();
        var transport = new ScriptedTransport([withStream]);
        var client = new RtspClient(transport, "192.168.100.1");
        await client.ConnectAsync(CancellationToken.None);

        await client.PlayAsync(CancellationToken.None);
        var stream = await client.ReadStreamAsync(CancellationToken.None);

        stream.ToArray().ShouldBe(new byte[] { 0x80, 0x1A, 0x00, 0x01 });
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

        public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
        {
            if (queued.Count == 0)
            {
                return Task.FromResult(0);
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

            return Task.FromResult(take);
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
