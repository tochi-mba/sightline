using System.Threading.Channels;
using Sightline.App.Services;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.App.Tests;

internal sealed class FakeCameraService : ICameraService
{
    private readonly Channel<CameraFrame> frames = Channel.CreateUnbounded<CameraFrame>();

    public IReadOnlyList<CameraConnectionOption> Found { get; set; } = [];

    public IReadOnlyList<CameraFile> CardFiles { get; set; } = [];

    public Exception? Failure { get; set; }

    public DeviceStatus Status { get; set; } = new([0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    public MenuCatalog Menu { get; set; } = MenuCatalog.Parse("""
        <Menu><Categories><Category><Name>System</Name><Settings>
          <Setting><Name>Frequency</Name><ID>0x0200</ID><Type>0</Type><Default>0</Default>
            <Values><Value><Name>50 Hz</Name><ID>0</ID></Value><Value><Name>60 Hz</Name><ID>1</ID></Value></Values>
          </Setting>
          <Setting><Name>Version</Name><ID>0x0209</ID><Type>3</Type><Default>0</Default>
            <Values><Value><Name>test firmware</Name><ID>0</ID></Value></Values>
          </Setting>
        </Settings></Category></Categories></Menu>
        """);

    public bool IsConnected { get; private set; }

    public int Disconnects { get; private set; }

    public int Photos { get; private set; }

    public int RecordingToggles { get; private set; }

    public (int Id, int Value)? WrittenSetting { get; private set; }

    public CameraFile? Downloaded { get; private set; }

    public CameraFile? Deleted { get; private set; }

    public TaskCompletionSource LiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<CameraConnectionOption>> FindCamerasAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FailOr(Found));

    public Task ConnectAsync(
        CameraConnectionOption option,
        string password,
        bool networkChangeConfirmed,
        CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CameraFrame> LiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        LiveStarted.TrySetResult();
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
        {
            yield return frame;
        }
    }

    public Task<DeviceStatus> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(FailOr(Status));

    public Task<MenuCatalog> MenuAsync(CancellationToken cancellationToken) => Task.FromResult(FailOr(Menu));

    public Task SetSettingAsync(int id, int value, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        WrittenSetting = (id, value);
        return Task.CompletedTask;
    }

    public Task PhotoAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Photos++;
        return Task.CompletedTask;
    }

    public Task<bool> ToggleRecordingAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        RecordingToggles++;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<CameraFile>> FilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FailOr(CardFiles));

    public Task<string> DownloadAsync(
        CameraFile file,
        string folder,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Downloaded = file;
        progress?.Report(1024);
        return Task.FromResult(Path.Combine(folder, file.DisplayName + ".jpg"));
    }

    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Deleted = file;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        Disconnects++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask ShowAsync(CameraFrame frame) => frames.Writer.WriteAsync(frame);

    private T FailOr<T>(T value)
    {
        ThrowIfFailing();
        return value;
    }

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}
