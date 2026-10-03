using System.Net;
using System.Runtime.CompilerServices;
using Sightline.Core;
using Sightline.Core.Connectivity;
using Sightline.Platform.Windows.Network;
using Sightline.Platform.Windows.Wlan;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.App.Services;

/// <summary>The real camera, over this PC's Wi-Fi.</summary>
public sealed class LiveCameraService : ICameraService
{
    /// <summary>What every camera network in this family is called at the start.</summary>
    public const string SsidPrefix = "ActionCam_";

    private readonly IWlanClient wlan;
    private readonly INetworkState network;
    private readonly CameraLink link;
    private CameraSession? session;

    /// <summary>The adapter that last reached the camera; the camera's DHCP remembers it.</summary>
    public Guid? RememberedAdapter { get; set; }

    /// <summary>Uses Windows' real Wi-Fi and network state.</summary>
    public LiveCameraService()
        : this(new WindowsWlanClient(), new SystemNetworkState(CameraAddress.Default))
    {
    }

    internal LiveCameraService(IWlanClient wlan, INetworkState network)
    {
        this.wlan = wlan ?? throw new ArgumentNullException(nameof(wlan));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        link = new CameraLink(wlan, network);
    }

    /// <inheritdoc />
    public bool IsConnected => session is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CameraConnectionOption>> FindCamerasAsync(CancellationToken cancellationToken)
    {
        // Scanning reads what is on the air; it never changes the network an adapter is on.
        var adapters = wlan.Adapters();
        await Task.WhenAll(adapters.Select(adapter => wlan.ScanAsync(adapter.Id, cancellationToken))).ConfigureAwait(false);

        // Each camera once per adapter that can see it, in the advisor's order — the choice that
        // changes least first — and, for the same adapter, the stronger signal first.
        var options = new List<CameraConnectionOption>();
        foreach (var ssid in adapters
                     .SelectMany(adapter => wlan.Networks(adapter.Id))
                     .Select(seen => seen.Ssid)
                     .Where(ssid => ssid.StartsWith(SsidPrefix, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal))
        {
            var ranked = AdapterAdvisor.Rank(
                AdapterAdvisor.Assess(adapters, ssid, adapter => network.InternetPathOtherThan(adapter.Id)),
                RememberedAdapter);
            foreach (var choice in ranked)
            {
                if (wlan.Networks(choice.Adapter.Id).FirstOrDefault(seen => seen.Ssid == ssid) is { } seen)
                {
                    options.Add(new CameraConnectionOption(
                        ssid, choice.Adapter.Id, choice.Adapter.Name, seen.SignalPercent,
                        choice.Explanation, choice.NeedsConsent, choice.StaysOnline));
                }
            }
        }

        return options
            .OrderBy(option => option.RequiresConsent)
            .ThenBy(option => option.StaysOnline ? 0 : 1)
            .ThenByDescending(option => option.SignalPercent)
            .ToList();
    }

    /// <inheritdoc />
    public async Task ConnectAsync(
        CameraConnectionOption option,
        string password,
        bool networkChangeConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (session is not null)
        {
            throw new InvalidOperationException("A camera is already connected.");
        }

        var adapter = wlan.Adapters().FirstOrDefault(candidate => candidate.Id == option.AdapterId)
            ?? throw new CameraNotReachableException($"{option.AdapterName} is no longer available. Was it unplugged?");
        var choice = AdapterAdvisor.Assess(
            [adapter],
            option.Ssid,
            candidate => network.InternetPathOtherThan(candidate.Id)).Single();

        IPAddress local;
        try
        {
            local = await link.JoinAsync(
                choice,
                option.Ssid,
                password,
                networkChangeConfirmed,
                CameraAddress.Default,
                cancellationToken).ConfigureAwait(false);
            session = await CameraSession.OpenAsync(CameraAddress.Default, local, cancellationToken).ConfigureAwait(false);
            RememberedAdapter = adapter.Id;
        }
        catch
        {
            await link.LeaveAsync().ConfigureAwait(false);
            throw;
        }
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
        return (await control.GetStatusAsync(cancellationToken).ConfigureAwait(false)).IsRecording;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CameraFile>> FilesAsync(CancellationToken cancellationToken) =>
        InBrowseModeAsync(control => control.GetFileListAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<string> DownloadAsync(
        CameraFile file,
        string folder,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);

        var temporary = Path.Combine(folder, $".{file.DisplayName}-{Guid.NewGuid():N}.part");
        try
        {
            await using (var destination = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, useAsync: true))
            {
                await InBrowseModeAsync(
                    control => control.DownloadAsync(file.Index, destination, progress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            var extension = DownloadedFileNaming.ExtensionFor(temporary);
            var final = DownloadedFileNaming.AvailablePath(folder, file.DisplayName, extension);
            File.Move(temporary, final);
            return final;
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(CameraFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return InBrowseModeAsync(
            async control =>
            {
                await control.DeleteFileAsync(file.Index, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            session = null;
        }

        await link.LeaveAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private CameraSession Open() =>
        session ?? throw new InvalidOperationException("Connect to the camera first.");

    private async Task<T> InBrowseModeAsync<T>(
        Func<GpSockConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var control = Open().Control;
        await control.SetModeAsync(CameraMode.Browse, cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(control).ConfigureAwait(false);
        }
        finally
        {
            // The caller's cancellation must still leave the camera ready for preview. A short,
            // independent deadline makes that best effort bounded instead of skipping it.
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await control.SetModeAsync(CameraMode.Record, restore.Token).ConfigureAwait(false);
        }
    }
}
