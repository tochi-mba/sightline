using Sightline.Core.Camera;

namespace Sightline.App.Services;

/// <summary>
/// The camera the person chose, read by the network link at every join: chosen on the connect panel, or
/// the last one used when the app opens.
/// </summary>
public sealed class CameraChoice
{
    private CameraTarget? current;

    /// <summary>The camera to join, or null before one is chosen.</summary>
    public CameraTarget? Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value);
    }
}
