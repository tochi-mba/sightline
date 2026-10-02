using System.Net;
using System.Runtime.CompilerServices;
using Sightline.Core;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.App.Services;

/// <summary>The real camera, over this PC's Wi-Fi.</summary>
public sealed class LiveCameraService : ICameraService
{
    /// <summary>What every camera network in this family is called at the start.</summary>
    public const string SsidPrefix = "ActionCam_";

    private CameraSession? session;
    private string? joinedAdapter;
    private string? joinedSsid;

    /// <inheritdoc />
    public bool IsConnected => session is not null;

    /// <inheritdoc />
    public IReadOnlyList<string> CamerasInRange()
    {
        var adapter = WindowsWifi.Choose(WindowsWifi.Adapters())?.Adapter.Name;
        return WindowsWifi.CamerasInRange(adapter, SsidPrefix);
    }

    /// <inheritdoc />
    public string NetworkAdvice()
    {
        var choice = WindowsWifi.Choose(WindowsWifi.Adapters());
        return choice switch
        {
            null => "This PC has no Wi-Fi adapter, so it cannot reach the camera.",
            { InterruptsInternet: true } =>
                $"{choice.Value.Adapter.Name} is this PC's only Wi-Fi and carries its internet, which will pause while the camera is connected.",
            _ => $"The camera will use {choice.Value.Adapter.Name}. Your internet is not affected.",
        };
    }

    /// <inheritdoc />
    public async Task ConnectAsync(string ssid, string password, CancellationToken cancellationToken)
    {
        if (CameraAddress.LocalAddressFor(CameraAddress.Default) is null)
        {
            var adapter = WindowsWifi.Choose(WindowsWifi.Adapters())?.Adapter
                ?? throw new CameraNotReachableException("This PC has no Wi-Fi adapter.");
            await Task.Run(() => WindowsWifi.Join(adapter.Name, ssid, password), cancellationToken)
                .ConfigureAwait(false);
            joinedAdapter = adapter.Name;
            joinedSsid = ssid;

            // DHCP takes a moment after the association. Polling the interface list is cheap and
            // it is the only signal there is that the address has arrived.
            for (var attempt = 0; attempt < 40 && CameraAddress.LocalAddressFor(CameraAddress.Default) is null; attempt++)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        session = await CameraSession.OpenAsync((IPAddress?)null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CameraFrame> LiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in Open().StreamFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <inheritdoc />
    public Task<DeviceStatus> StatusAsync(CancellationToken cancellationToken) =>
        Open().Control.GetStatusAsync(cancellationToken);

    /// <inheritdoc />
    public Task<MenuCatalog> MenuAsync(CancellationToken cancellationToken) =>
        Open().Control.GetMenuAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetSettingAsync(int id, int value, CancellationToken cancellationToken) =>
        Open().Control.SetSettingAsync(id, value, cancellationToken);

    /// <inheritdoc />
    public Task PhotoAsync(CancellationToken cancellationToken) =>
        Open().Control.CapturePictureAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ToggleRecordingAsync(CancellationToken cancellationToken)
    {
        var control = Open().Control;
        await control.ToggleRecordingAsync(cancellationToken).ConfigureAwait(false);
        return (await control.GetStatusAsync(cancellationToken).ConfigureAwait(false)).IsBusy;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            session = null;
        }

        if (joinedAdapter is not null && joinedSsid is not null)
        {
            var adapter = joinedAdapter;
            var ssid = joinedSsid;
            await Task.Run(() => WindowsWifi.Leave(adapter, ssid)).ConfigureAwait(false);
            joinedAdapter = joinedSsid = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private CameraSession Open() =>
        session ?? throw new InvalidOperationException("Connect to the camera first.");
}
