using System.Net;

namespace Sightline.Core.Connectivity;

/// <summary>Why joining the camera's Wi-Fi did not work.</summary>
public enum LinkFailure
{
    /// <summary>Windows could not join the network: wrong password, out of range, or the camera is taken.</summary>
    NotJoined,

    /// <summary>
    /// The adapter joined but the camera gave it no address. Seen on the reference camera when
    /// another device had just been connected to it: it looks after one device at a time.
    /// </summary>
    NoAddress,
}

/// <summary>Joining the camera's Wi-Fi failed, and the message says why and what to do.</summary>
public sealed class CameraLinkException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CameraLinkException(LinkFailure failure, string message) : base(message)
    {
        Failure = failure;
    }

    /// <summary>Creates the exception.</summary>
    public CameraLinkException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public CameraLinkException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CameraLinkException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>What went wrong.</summary>
    public LinkFailure Failure { get; }
}

/// <summary>What a join changed, so that leaving can undo exactly that.</summary>
/// <param name="AdapterId">The adapter on the camera.</param>
/// <param name="AdapterName">Its name, for messages.</param>
/// <param name="Previous">The network it was taken from, to put it back on.</param>
/// <param name="ConnectedByUs">Whether Sightline connected it, and so should disconnect it.</param>
/// <param name="SavedProfile">Sightline's profile to delete, if it saved one.</param>
public sealed record LinkState(Guid AdapterId, string AdapterName, WifiConnection? Previous, bool ConnectedByUs, string? SavedProfile);

/// <summary>How long joining may take, and how often to look.</summary>
/// <param name="AddressTimeout">How long to wait for the camera to hand out an address.</param>
/// <param name="PollInterval">How often to look for it.</param>
public sealed record CameraLinkTiming(TimeSpan AddressTimeout, TimeSpan PollInterval)
{
    /// <summary>The real timings: the camera's DHCP answers within a few seconds when it answers at all.</summary>
    public static CameraLinkTiming Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Puts one adapter on the camera's Wi-Fi, and puts it back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Three promises, each because breaking it once took somebody offline:
/// </para>
/// <list type="number">
/// <item>Only the adapter that was chosen is ever touched.</item>
/// <item>
/// Only Sightline's own profile is saved or deleted (see <see cref="WlanProfile"/>); the person's
/// saved networks are never overwritten or removed.
/// </item>
/// <item>
/// An adapter taken off another network is put back on that network, by its own profile, when the
/// camera is left — including when joining fails part-way.
/// </item>
/// </list>
/// </remarks>
public sealed class CameraLink
{
    private readonly IWlanClient wlan;
    private readonly INetworkState network;
    private readonly CameraLinkTiming timing;
    private WifiAdapter? adapter;
    private WifiConnection? previous;
    private bool connectedByUs;
    private string? savedProfile;

    /// <summary>Creates a link over the given Wi-Fi and network state.</summary>
    public CameraLink(IWlanClient wlan, INetworkState network, CameraLinkTiming? timing = null)
    {
        this.wlan = wlan ?? throw new ArgumentNullException(nameof(wlan));
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.timing = timing ?? CameraLinkTiming.Default;
    }

    /// <summary>The adapter on the camera, while joined.</summary>
    public WifiAdapter? Adapter => adapter;

    /// <summary>
    /// Sightline's profile that the last <see cref="LeaveAsync"/> could not delete, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The profile holds the camera's password, so a failure to remove it is said out loud rather than
    /// only traced: whoever is leaving can tell the person, and how to delete it themselves.
    /// </remarks>
    public string? ProfileLeftBehind { get; private set; }

    /// <summary>
    /// Everything leaving needs to know, while joined; null otherwise.
    /// </summary>
    /// <remarks>
    /// The command line joins in one process and leaves in another, so what the join changed has to
    /// survive in between — otherwise the second process could not put the adapter back.
    /// </remarks>
    public LinkState? State =>
        adapter is { } joined ? new LinkState(joined.Id, joined.Name, previous, connectedByUs, savedProfile) : null;

    /// <summary>Takes over a join another process made, so this one can leave it properly.</summary>
    /// <exception cref="InvalidOperationException">Already joined.</exception>
    public void Resume(LinkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (adapter is not null)
        {
            throw new InvalidOperationException("Already joined to a camera; leave it first.");
        }

        adapter = new WifiAdapter(state.AdapterId, state.AdapterName, "", false, null);
        previous = state.Previous;
        connectedByUs = state.ConnectedByUs;
        savedProfile = state.SavedProfile is { } profile && WlanProfile.IsOurs(profile) ? profile : null;
    }

    /// <summary>
    /// Joins <paramref name="ssid"/> on the chosen adapter and returns this PC's address there.
    /// </summary>
    /// <param name="choice">The adapter, as <see cref="AdapterAdvisor"/> assessed it.</param>
    /// <param name="ssid">The camera's network name.</param>
    /// <param name="password">Its password, or empty for an open network.</param>
    /// <param name="consented">Whether the person accepted the choice's consequence, when it has one.</param>
    /// <param name="camera">The camera's address; its /24 is the camera's network.</param>
    /// <param name="cancellationToken">Gives up; the adapter is put back as it was.</param>
    /// <exception cref="InvalidOperationException">Already joined, or consent was needed and not given.</exception>
    /// <exception cref="CameraLinkException">The camera could not be joined; the adapter has been put back.</exception>
    public async Task<IPAddress> JoinAsync(
        AdapterChoice choice, string ssid, string? password, bool consented, IPAddress camera,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentException.ThrowIfNullOrEmpty(ssid);
        ArgumentNullException.ThrowIfNull(camera);
        if (adapter is not null)
        {
            throw new InvalidOperationException("Already joined to a camera; leave it first.");
        }

        if (choice.NeedsConsent && !consented)
        {
            throw new InvalidOperationException(
                $"{choice.Adapter.Name} is on {choice.Adapter.Connection?.Ssid}; using it needs the person's say-so first.");
        }

        adapter = choice.Adapter;
        try
        {
            if (choice.Verdict != AdapterVerdict.OnCamera)
            {
                var profile = WlanProfile.NameFor(ssid);
                var xml = WlanProfile.Xml(ssid, password);
                previous = choice.Adapter.Connection;
                wlan.SaveProfile(choice.Adapter.Id, xml);
                savedProfile = profile;
                connectedByUs = true;
                var result = await wlan.ConnectAsync(choice.Adapter.Id, profile, ssid, cancellationToken).ConfigureAwait(false);
                if (!result.Joined)
                {
                    throw new CameraLinkException(LinkFailure.NotJoined,
                        $"{choice.Adapter.Name} could not join {ssid}{(result.Reason is null ? "" : $" ({result.Reason})")}. "
                        + "Check the password on the camera's screen, keep the camera close, and disconnect any other phone or app from it.");
                }
            }

            return await WaitForAddressAsync(choice.Adapter, ssid, camera, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await LeaveAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Receives one line per step, for the diagnostics page, when set.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Leaves the camera: disconnects if Sightline connected, deletes Sightline's profile, and puts the
    /// adapter back on the network it was taken from.
    /// </summary>
    /// <remarks>
    /// Each step is attempted whatever happened to the one before: a dongle pulled out mid-session
    /// fails the disconnect, and that must not stop Sightline deleting its profile or a built-in
    /// adapter going back on its network.
    /// </remarks>
    /// <returns>The network the adapter was put back on, or null when there was none to restore.</returns>
    public async Task<string?> LeaveAsync()
    {
        if (adapter is not { } joined)
        {
            return null;
        }

        var restored = (string?)null;
        ProfileLeftBehind = null;
        if (connectedByUs)
        {
            Attempt($"disconnect {joined.Name}", () => wlan.Disconnect(joined.Id));
        }

        if (savedProfile is { } profile)
        {
            if (!Attempt($"delete profile '{profile}' from {joined.Name}", () => wlan.DeleteProfile(joined.Id, profile)))
            {
                ProfileLeftBehind = profile;
            }
        }

        if (previous is { } restore)
        {
            // If the old network has gone out of range Windows rejoins it by itself when it is back,
            // because its own profile was never touched; failing here is not fatal.
            try
            {
                var result = await wlan.ConnectAsync(joined.Id, restore.Profile, restore.Ssid, CancellationToken.None)
                    .ConfigureAwait(false);
                Trace?.Invoke($"put {joined.Name} back on {restore.Ssid}: {(result.Joined ? "done" : result.Reason)}");
                restored = result.Joined ? restore.Ssid : null;
            }
            catch (WlanException exception)
            {
                Trace?.Invoke($"put {joined.Name} back on {restore.Ssid}: failed ({exception.Message})");
            }
        }

        adapter = null;
        previous = null;
        connectedByUs = false;
        savedProfile = null;
        return restored;
    }

    /// <summary>Runs one step of leaving, traces how it went, and says whether it worked.</summary>
    private bool Attempt(string step, Action action)
    {
        try
        {
            action();
            Trace?.Invoke($"{step}: done");
            return true;
        }
        catch (WlanException exception)
        {
            Trace?.Invoke($"{step}: failed ({exception.Message})");
            return false;
        }
    }

    private async Task<IPAddress> WaitForAddressAsync(
        WifiAdapter joined, string ssid, IPAddress camera, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timing.AddressTimeout;
        while (true)
        {
            var addresses = network.AddressesOn(joined.Id);
            if (CameraAddress.LocalAddressFor(camera, addresses) is { } local)
            {
                return local;
            }

            if (DateTime.UtcNow >= deadline)
            {
                var selfAssigned = addresses.Any(a => a.GetAddressBytes() is [169, 254, ..]);
                throw new CameraLinkException(LinkFailure.NoAddress,
                    $"{joined.Name} joined {ssid}, but the camera gave it no address{(selfAssigned ? " (Windows fell back to a 169.254 one)" : "")}. "
                    + "The camera looks after one device at a time: disconnect any other phone, tablet or PC from it — or switch the camera off and on — and try again.");
            }

            await Task.Delay(timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
