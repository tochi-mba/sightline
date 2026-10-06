using System.Net;
using Sightline.Core.Connectivity;
using Sightline.Protocol;

namespace Sightline.Core.Camera;

/// <summary>The camera to join and how, as the person chose it on the connect panel.</summary>
/// <param name="AdapterId">The Wi-Fi adapter to reach it through.</param>
/// <param name="Ssid">The camera's network name.</param>
/// <param name="Password">Its password, shown on the camera's screen.</param>
/// <param name="Consented">
/// Whether the person accepted that the adapter leaves the network it is on, for an adapter that must.
/// </param>
public sealed record CameraTarget(Guid AdapterId, string Ssid, string Password, bool Consented);

/// <summary>
/// The controller's way onto the camera's network on Windows: the chosen adapter joins the camera, and
/// every socket to the camera is sent from that adapter's own address, so nothing else on the PC moves.
/// </summary>
/// <remarks>
/// Each join is a <see cref="CameraLink"/>, so its promises hold here too: only the chosen adapter is
/// touched, only Sightline's own profile is saved or deleted, and an adapter taken off another network
/// is put back on it when the camera is left, a lost camera included.
/// </remarks>
public sealed class WifiCameraLink : ICameraLink
{
    private readonly IWlanClient wlan;
    private readonly INetworkState network;
    private readonly Func<CameraTarget?> target;
    private readonly CameraLinkTiming? joinTiming;
    private readonly TimeSpan lossCheck;

    /// <summary>Creates the link.</summary>
    /// <param name="wlan">This PC's Wi-Fi.</param>
    /// <param name="network">Its addresses and routes.</param>
    /// <param name="target">The camera to join, read at every join; null until one is chosen.</param>
    /// <param name="joinTiming">How long joining may take; the real values when omitted.</param>
    /// <param name="lossCheck">How often a held network is checked for still being there; a second when omitted.</param>
    public WifiCameraLink(
        IWlanClient wlan,
        INetworkState network,
        Func<CameraTarget?> target,
        CameraLinkTiming? joinTiming = null,
        TimeSpan? lossCheck = null)
    {
        this.wlan = wlan ?? throw new ArgumentNullException(nameof(wlan));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        this.joinTiming = joinTiming;
        this.lossCheck = lossCheck ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc />
    /// <exception cref="CameraLinkException">No camera chosen, its adapter gone, or the camera not joined.</exception>
    public async Task<ICameraLease> JoinAsync(bool reconnecting, CancellationToken cancellationToken)
    {
        var chosen = target() ?? throw new CameraLinkException(
            LinkFailure.NotJoined, "Choose a camera, and the Wi-Fi adapter to reach it through, first.");
        var adapter = wlan.Adapters().FirstOrDefault(candidate => candidate.Id == chosen.AdapterId)
            ?? throw new CameraLinkException(
                LinkFailure.NotJoined, "The Wi-Fi adapter chosen for the camera is no longer there. Was it unplugged?");
        var choice = AdapterAdvisor.Assess([adapter], chosen.Ssid, candidate => network.InternetPathOtherThan(candidate.Id)).Single();

        var link = new CameraLink(wlan, network, joinTiming);
        var local = await link.JoinAsync(choice, chosen.Ssid, chosen.Password, chosen.Consented, CameraAddress.Default, cancellationToken)
            .ConfigureAwait(false);
        return new Lease(link, adapter.Id, local, network, lossCheck);
    }

    /// <summary>The camera's network while it is held: sockets from this PC's address there, and its loss.</summary>
    private sealed class Lease : ICameraLease
    {
        private readonly CameraLink link;
        private readonly IPAddress local;
        private readonly CancellationTokenSource watching = new();
        private readonly TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task watch;

        public Lease(CameraLink link, Guid adapter, IPAddress local, INetworkState network, TimeSpan every)
        {
            this.link = link;
            this.local = local;
            watch = WatchAsync(adapter, network, every, watching.Token);
        }

        public Task Lost => lost.Task;

        public ICameraTransport Transport(int port) =>
            new TcpCameraTransport(new IPEndPoint(CameraAddress.Default, port), local);

        public ICameraDatagrams Datagrams() => new UdpCameraDatagrams(CameraAddress.Default, local);

        public async ValueTask DisposeAsync()
        {
            await watching.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
            watching.Dispose();
            await link.LeaveAsync().ConfigureAwait(false);
        }

        /// <summary>Waits for the adapter to lose its address on the camera's network, as it does when the camera sleeps.</summary>
        private async Task WatchAsync(Guid adapter, INetworkState network, TimeSpan every, CancellationToken cancellationToken)
        {
            try
            {
                while (CameraAddress.LocalAddressFor(CameraAddress.Default, network.AddressesOn(adapter)) is not null)
                {
                    await Task.Delay(every, cancellationToken).ConfigureAwait(false);
                }

                lost.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Let go of on purpose: not a loss.
            }
        }
    }
}
