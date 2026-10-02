using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.App.Services;

/// <summary>
/// Everything the window needs from a camera, behind one seam.
/// </summary>
/// <remarks>
/// The view model talks only to this, so it can be driven by a fake camera in tests and by the
/// real one in the app, with no Wi-Fi, sockets or threads in between that a test cannot control.
/// </remarks>
public interface ICameraService : IAsyncDisposable
{
    /// <summary>Camera networks currently in range.</summary>
    IReadOnlyList<string> CamerasInRange();

    /// <summary>
    /// What joining a camera will do to this PC's internet, in a sentence, before it happens.
    /// </summary>
    string NetworkAdvice();

    /// <summary>Puts this PC on the camera's Wi-Fi and opens the control channel.</summary>
    Task ConnectAsync(string ssid, string password, CancellationToken cancellationToken);

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
