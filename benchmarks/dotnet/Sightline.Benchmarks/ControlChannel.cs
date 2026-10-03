using BenchmarkDotNet.Attributes;
using Sightline.Benchmarks.Camera;
using Sightline.Protocol.GpSock;

namespace Sightline.Benchmarks;

/// <summary>
/// Calls the apps make on the control channel, timed from the request to the parsed answer.
/// </summary>
/// <remarks>
/// <para>
/// The camera here answers at once from memory, so each figure is Sightline's share of what a
/// person waits for and none of the camera's or the Wi-Fi's.
/// </para>
/// <list type="bullet">
/// <item>A status round trip is the smallest exchange there is: one request, one 16-byte answer,
/// which is how the apps learn the camera's mode and whether it is recording.</item>
/// <item>Reading the whole menu stands between connecting and the settings screen: the reference
/// camera's 18,523-byte menu, in pieces of 242 bytes, then parsed.</item>
/// <item>A download is timed per megabyte, so its operations a second are the megabytes a second
/// Sightline's side could keep up with. The file is 8 MB in 60 KB frames and is written nowhere,
/// leaving the disk out of it too.</item>
/// <item>Listing 200 files is opening the card library on a well-used card.</item>
/// </list>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet decides a benchmark's lifetime and never disposes one. Its end is " +
                    "[GlobalCleanup], which closes the connection; a Dispose nothing calls would only look safer.")]
public class ControlChannel
{
    private const int FileBytes = 8 * 1024 * 1024;
    private const int Megabytes = FileBytes / (1024 * 1024);
    private const int Files = 200;

    private GpSockConnection connection = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var camera = new ScriptedCamera();
        camera.Answer(
            GpSockCommand.GetDeviceStatus,
            Answers.Ack(GpSockCommand.GetDeviceStatus, ReferenceCamera.Status()));
        camera.Answer(
            GpSockCommand.GetParameterFile,
            Answers.Chunked(GpSockCommand.GetParameterFile, ReferenceCamera.Menu(), GpSockConnection.MaxChunkPayload));
        camera.Answer(
            GpSockCommand.PlaybackGetRawData,
            Answers.Chunked(GpSockCommand.PlaybackGetRawData, new byte[FileBytes], Answers.DownloadFrameBytes));
        camera.Answer(
            GpSockCommand.PlaybackGetFileCount,
            Answers.Ack(GpSockCommand.PlaybackGetFileCount, [Files & 0xFF, Files >> 8]));
        camera.AnswerFileList(Answers.FileListPages(Files));
        connection = new GpSockConnection(camera);
        await connection.OpenAsync();

        Expect.Equal(CameraMode.Record, (await StatusRoundTrip()).Mode, "the reference camera's mode");
        Expect.Equal(21, (await ReadWholeMenu()).Settings.Count, "settings in the reference menu");
        Expect.Equal((long)FileBytes, await DownloadMegabyte(), "bytes downloaded");
        Expect.Equal(Files, (await ListTwoHundredFiles()).Count, "files listed");
    }

    [GlobalCleanup]
    public Task Cleanup() => connection.DisposeAsync().AsTask();

    /// <summary>Asking the camera how it is, and reading the answer.</summary>
    [Benchmark]
    public Task<DeviceStatus> StatusRoundTrip() => connection.GetStatusAsync();

    /// <summary>The camera's whole settings menu, gathered and parsed.</summary>
    [Benchmark]
    public Task<MenuCatalog> ReadWholeMenu() => connection.GetMenuAsync();

    /// <summary>One megabyte of a file coming off the card.</summary>
    [Benchmark(OperationsPerInvoke = Megabytes)]
    public Task<long> DownloadMegabyte() => connection.DownloadAsync(1, Stream.Null);

    /// <summary>Every file on a card holding 200, a page at a time.</summary>
    [Benchmark]
    public Task<IReadOnlyList<CameraFile>> ListTwoHundredFiles() => connection.GetFileListAsync();
}
