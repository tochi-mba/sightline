using Sightline.Protocol;
using Sightline.Protocol.GpSock;

namespace Sightline.Benchmarks.Camera;

/// <summary>
/// The camera's control port, answering every request from bytes encoded in advance.
/// </summary>
/// <remarks>
/// The test suite's FakeCamera models the firmware: modes, refusals, transfers that can be
/// interrupted. This models nothing. It reads the command, finds the answer and replays it, because
/// a benchmark of the protocol should time the protocol, and a fake that parsed, allocated or
/// decided per request would put its own cost into every figure. It answers instantly, so what a
/// benchmark reports is Sightline's share of a round trip and none of the camera's.
/// </remarks>
internal sealed class ScriptedCamera : ICameraTransport
{
    private readonly Dictionary<GpSockCommand, byte[]> answers = [];
    private byte[][] fileListPages = [];
    private byte[] answer = [];
    private int sent;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>Answers <paramref name="command"/> with <paramref name="wire"/>, every time it is asked.</summary>
    public void Answer(GpSockCommand command, byte[] wire) => answers[command] = wire;

    /// <summary>
    /// Answers the file list a page at a time, each from <see cref="Answers.FileListPages"/>.
    /// </summary>
    public void AnswerFileList(byte[][] pages) => fileListPages = pages;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var request = bytes.Span;
        var command = (GpSockCommand)((request[10] << 8) | request[11]);
        answer = command == GpSockCommand.PlaybackGetFileList
            ? Page(request[GpSockFrame.RequestHeaderLength..])
            : answers[command];
        sent = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> ReceiveAsync(Memory<byte> into, CancellationToken cancellationToken)
    {
        var count = Math.Min(into.Length, answer.Length - sent);
        answer.AsSpan(sent, count).CopyTo(into.Span);
        sent += count;
        return CompletedReads.Of(count);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The page a list request asks for: the first by a flag, each later one by the index of the
    /// last file already seen, as GpSockConnection pages through the card.
    /// </summary>
    private byte[] Page(ReadOnlySpan<byte> request)
    {
        var after = request[0] == 1 ? 0 : request[1] | (request[2] << 8);
        return fileListPages[after / Answers.FilesPerPage];
    }
}
