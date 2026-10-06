using Sightline.Core.Camera;

namespace Sightline.Core.Connectivity;

/// <summary>One camera seen by one Wi-Fi adapter, and what using that adapter would do.</summary>
/// <param name="Ssid">The camera's network name.</param>
/// <param name="Choice">The adapter, as <see cref="AdapterAdvisor"/> assessed it for this camera.</param>
/// <param name="SignalPercent">How well the adapter hears the camera, 0 to 100.</param>
public sealed record CameraOption(string Ssid, AdapterChoice Choice, int SignalPercent)
{
    /// <summary>The adapter's id.</summary>
    public Guid AdapterId => Choice.Adapter.Id;

    /// <summary>The adapter's name, as Windows shows it.</summary>
    public string AdapterName => Choice.Adapter.Name;

    /// <summary>What connecting through this adapter changes, in plain words, said before it happens.</summary>
    public string Explanation => Choice.Explanation;

    /// <summary>Whether the adapter must leave another network, so the person is asked first.</summary>
    public bool RequiresConsent => Choice.NeedsConsent;

    /// <summary>Whether this PC keeps an internet connection if it is used.</summary>
    public bool StaysOnline => Choice.StaysOnline;

    /// <summary>A label for a picker.</summary>
    public string DisplayName => $"{Ssid}  ·  {AdapterName}  ·  {SignalPercent}%";

    /// <summary>The camera to join, through this adapter, with <paramref name="password"/>.</summary>
    public CameraTarget Target(string password, bool consented) => new(AdapterId, Ssid, password, consented);
}

/// <summary>
/// Finds the cameras in range of each Wi-Fi adapter, without changing any connection, and offers the
/// choice that changes least first.
/// </summary>
public sealed class CameraFinder
{
    /// <summary>What every camera network in this family is called at the start.</summary>
    public const string SsidPrefix = "ActionCam_";

    private readonly IWlanClient wlan;
    private readonly INetworkState network;

    /// <summary>Looks through <paramref name="wlan"/>, judging adapters by <paramref name="network"/>.</summary>
    public CameraFinder(IWlanClient wlan, INetworkState network)
    {
        this.wlan = wlan ?? throw new ArgumentNullException(nameof(wlan));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
    }

    /// <summary>
    /// Returns each camera once per adapter that can see it, best first: in <see cref="AdapterAdvisor.Rank"/>'s
    /// order, the stronger signal first between otherwise equal choices.
    /// </summary>
    /// <remarks>
    /// Only adapters on no network are asked to scan. A scan takes an adapter off its channel for a moment,
    /// and an adapter on a network may be how this PC is online, so for those the networks Windows has
    /// already seen on its own are used instead. Looking for a camera never disturbs a connection.
    /// </remarks>
    /// <param name="remembered">The adapter that last reached a camera, whose DHCP server remembers it.</param>
    /// <param name="cancellationToken">Gives up scanning.</param>
    public async Task<IReadOnlyList<CameraOption>> FindAsync(Guid? remembered, CancellationToken cancellationToken)
    {
        // Scanning reads what is on the air; it never changes the network an adapter is on.
        var adapters = wlan.Adapters();
        await Task.WhenAll(adapters.Where(adapter => adapter.IsIdle).Select(adapter => wlan.ScanAsync(adapter.Id, cancellationToken)))
            .ConfigureAwait(false);

        var seen = adapters.ToDictionary(adapter => adapter.Id, adapter => wlan.Networks(adapter.Id));
        var cameras = seen.Values
            .SelectMany(networks => networks)
            .Select(found => found.Ssid)
            .Where(ssid => ssid.StartsWith(SsidPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

        var options = new List<CameraOption>();
        foreach (var ssid in cameras)
        {
            foreach (var choice in AdapterAdvisor.Assess(adapters, ssid, adapter => network.InternetPathOtherThan(adapter.Id)))
            {
                if (seen[choice.Adapter.Id].FirstOrDefault(found => found.Ssid == ssid) is { } heard)
                {
                    options.Add(new CameraOption(ssid, choice, heard.SignalPercent));
                }
            }
        }

        var ranked = AdapterAdvisor.Rank(options.Select(option => option.Choice).Distinct().ToList(), remembered).ToList();
        return options
            .OrderBy(option => ranked.IndexOf(option.Choice))
            .ThenByDescending(option => option.SignalPercent)
            .ThenBy(option => option.Ssid, StringComparer.Ordinal)
            .ToList();
    }
}
