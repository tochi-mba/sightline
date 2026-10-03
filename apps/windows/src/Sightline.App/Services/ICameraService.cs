using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.App.Services;

/// <summary>One camera seen by one Wi-Fi adapter, with the consequence of using it.</summary>
/// <param name="Ssid">The camera network.</param>
/// <param name="AdapterId">The stable Windows interface id.</param>
/// <param name="AdapterName">The name shown by Windows.</param>
/// <param name="SignalPercent">The signal quality Windows reports.</param>
/// <param name="Explanation">What connecting through this adapter changes, in plain language.</param>
/// <param name="RequiresConsent">Whether the adapter must leave another network first.</param>
/// <param name="StaysOnline">Whether another connection keeps this PC online.</param>
public sealed record CameraConnectionOption(
    string Ssid,
    Guid AdapterId,
    string AdapterName,
    int SignalPercent,
    string Explanation,
    bool RequiresConsent,
    bool StaysOnline)
{
    /// <summary>A compact label for a picker.</summary>
    public string DisplayName => $"{Ssid}  ·  {AdapterName}  ·  {SignalPercent}%";
}

/// <summary>
/// Everything the window needs from a camera, behind one seam.
/// </summary>
/// <remarks>
/// The view model talks only to this, so it can be driven by a fake camera in tests and by the
/// real one in the app, with no Wi-Fi, sockets or threads in between that a test cannot control.
/// </remarks>
public interface ICameraService : IAsyncDisposable
{
    /// <summary>Scans every adapter without changing any connection, then returns cameras in range.</summary>
    Task<IReadOnlyList<CameraConnectionOption>> FindCamerasAsync(CancellationToken cancellationToken);

    /// <summary>Puts this PC on the camera's Wi-Fi and opens the control channel.</summary>
    Task ConnectAsync(
        CameraConnectionOption option,
        string password,
        bool networkChangeConfirmed,
        CancellationToken cancellationToken);

    /// <summary>Whether a session is open.</summary>
    bool IsConnected { get; }

    /// <summary>The live picture, until cancelled.</summary>
    IAsyncEnumerable<CameraFrame> LiveAsync(CancellationToken cancellationToken);

    /// <summary>The camera's state.</summary>
    Task<DeviceStatus> StatusAsync(CancellationToken cancellationToken);

    /// <summary>The camera's own settings catalogue.</summary>
    Task<MenuCatalog> MenuAsync(CancellationToken cancellationToken);

    /// <summary>Writes one setting.</summary>
    Task SetSettingAsync(int id, int value, CancellationToken cancellationToken);

    /// <summary>Takes a photo onto the camera's card.</summary>
    Task PhotoAsync(CancellationToken cancellationToken);

    /// <summary>Starts or stops recording to the card, returning whether it is now recording.</summary>
    Task<bool> ToggleRecordingAsync(CancellationToken cancellationToken);

    /// <summary>Ends the session and leaves the camera's Wi-Fi.</summary>
    Task DisconnectAsync();
}
