using System.Xml;
using System.Xml.Linq;
using Sightline.Core.Connectivity;
using Sightline.Platform.Windows.Processes;

namespace Sightline.Platform.Windows.Wlan;

/// <summary>How long Windows is given to scan and to join.</summary>
/// <param name="ScanWait">How long a scan takes; Windows promises results within about four seconds.</param>
/// <param name="JoinTimeout">How long to wait for an adapter to report it has joined.</param>
/// <param name="PollInterval">How often to look.</param>
internal sealed record WlanClientTiming(TimeSpan ScanWait, TimeSpan JoinTimeout, TimeSpan PollInterval)
{
    /// <summary>The real timings.</summary>
    public static WlanClientTiming Default { get; } =
        new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// This PC's Wi-Fi: read through the Native Wi-Fi API, changed through netsh.
/// </summary>
/// <remarks>
/// <para>
/// Two guards here hold whatever a caller asks: a profile is only ever saved or deleted if its name
/// is Sightline's own (<see cref="WlanProfile.IsOurs"/>). A person's saved networks cannot be
/// overwritten or removed by this class, by mistake or otherwise.
/// </para>
/// </remarks>
public sealed class WindowsWlanClient : IWlanClient
{
    private readonly IWlanReader reader;
    private readonly NetshWlanWriter writer;
    private readonly IAdapterDirectory directory;
    private readonly WlanClientTiming timing;

    /// <summary>A client over this PC's real Wi-Fi.</summary>
    public WindowsWlanClient()
        : this(new WlanReader(), new NetshWlanWriter(new ProcessCommandRunner()), new AdapterDirectory(), WlanClientTiming.Default)
    {
    }

    internal WindowsWlanClient(IWlanReader reader, NetshWlanWriter writer, IAdapterDirectory directory, WlanClientTiming timing)
    {
        this.reader = reader;
        this.writer = writer;
        this.directory = directory;
        this.timing = timing;
    }

    /// <inheritdoc />
    public IReadOnlyList<WifiAdapter> Adapters() =>
        reader.Interfaces()
            .Select(row => new WifiAdapter(
                row.Id,
                directory.NameOf(row.Id) ?? row.Description,
                row.Description,
                directory.IsExternal(row.Id),
                row.State == WlanLayout.Connected && reader.Connection(row.Id) is { } current
                    ? new WifiConnection(current.Ssid, current.Profile)
                    : null))
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<WifiNetwork> Networks(Guid adapter) =>
        reader.Networks(adapter)
            .Where(network => network.Ssid.Length > 0)
            .GroupBy(network => network.Ssid, StringComparer.Ordinal)
            .Select(group => new WifiNetwork(group.Key, group.Max(network => network.SignalPercent)))
            .OrderByDescending(network => network.SignalPercent)
            .ToList();

    /// <inheritdoc />
    public async Task ScanAsync(Guid adapter, CancellationToken cancellationToken)
    {
        reader.Scan(adapter);
        await Task.Delay(timing.ScanWait, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SaveProfile(Guid adapter, string profileXml)
    {
        ArgumentNullException.ThrowIfNull(profileXml);
        if (!WlanProfile.IsOurs(ProfileNameIn(profileXml)))
        {
            throw new InvalidOperationException("Sightline saves only its own Wi-Fi profiles.");
        }

        writer.AddProfile(NameOf(adapter), profileXml);
    }

    /// <inheritdoc />
    public async Task<WlanConnectResult> ConnectAsync(Guid adapter, string profile, string ssid, CancellationToken cancellationToken)
    {
        writer.Connect(NameOf(adapter), profile, ssid);
        var deadline = DateTime.UtcNow + timing.JoinTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (reader.Connection(adapter) is { } current && current.Ssid == ssid)
            {
                return new WlanConnectResult(true, null);
            }

            await Task.Delay(timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return new WlanConnectResult(false, $"Windows had not joined it after {timing.JoinTimeout.TotalSeconds:0} seconds");
    }

    /// <inheritdoc />
    public void Disconnect(Guid adapter) => writer.Disconnect(NameOf(adapter));

    /// <inheritdoc />
    public void DeleteProfile(Guid adapter, string profile)
    {
        if (!WlanProfile.IsOurs(profile))
        {
            throw new InvalidOperationException($"Sightline deletes only its own Wi-Fi profiles, not '{profile}'.");
        }

        writer.DeleteProfile(NameOf(adapter), profile);
    }

    private string NameOf(Guid adapter) =>
        directory.NameOf(adapter) ?? throw new WlanException("That Wi-Fi adapter is no longer there. Was it unplugged?");

    /// <summary>The profile's own name — the root's name element, not the network's inside SSIDConfig.</summary>
    private static string ProfileNameIn(string profileXml)
    {
        try
        {
            var root = XDocument.Parse(profileXml).Root!;
            return root.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? "";
        }
        catch (XmlException)
        {
            return "";
        }
    }
}
