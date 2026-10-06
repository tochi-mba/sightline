using Sightline.Protocol;

namespace Sightline.Core.Camera;

/// <summary>
/// Puts this PC on the camera's network, in whatever way the person chose: which adapter, which camera,
/// with consent where an adapter must leave another network.
/// </summary>
/// <remarks>
/// A seam so the controller above it is tested without Wi-Fi, and so nothing above it can reach for a call
/// that takes the PC offline.
/// </remarks>
public interface ICameraLink
{
    /// <summary>Joins the camera's network.</summary>
    /// <param name="reconnecting">Whether this is getting back a camera that was lost, which needs no new consent.</param>
    /// <param name="cancellationToken">Gives the join up and leaves the network.</param>
    Task<ICameraLease> JoinAsync(bool reconnecting, CancellationToken cancellationToken);
}

/// <summary>
/// The camera's network, held until disposed: connections and sockets sent from this PC's address on it.
/// </summary>
public interface ICameraLease : ICameraSockets, IAsyncDisposable
{
    /// <summary>Completes once the camera's network is known to be lost.</summary>
    Task Lost { get; }
}
