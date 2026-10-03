namespace Sightline.Core.Connectivity;

/// <summary>What putting an adapter on the camera would do.</summary>
public enum AdapterVerdict
{
    /// <summary>It is already on the camera; nothing needs to change.</summary>
    OnCamera,

    /// <summary>It is on no network; using it changes nothing else on this PC.</summary>
    Free,

    /// <summary>
    /// It is on another network, which it would have to leave. Never chosen without the person's
    /// explicit say-so, because that network may be how this PC reaches the internet.
    /// </summary>
    LeavesNetwork,
}

/// <summary>One adapter, what using it for the camera would do, and that in a sentence.</summary>
/// <param name="Adapter">The adapter.</param>
/// <param name="Verdict">What using it would do.</param>
/// <param name="StaysOnline">Whether this PC keeps an internet connection if it is used.</param>
/// <param name="Explanation">The consequence in plain words, shown before anything happens.</param>
public sealed record AdapterChoice(WifiAdapter Adapter, AdapterVerdict Verdict, bool StaysOnline, string Explanation)
{
    /// <summary>Whether using it needs the person to confirm they accept the consequence first.</summary>
    public bool NeedsConsent => Verdict == AdapterVerdict.LeavesNetwork;
}

/// <summary>
/// Decides which Wi-Fi adapter the camera should use, and says what each choice would cost.
/// </summary>
/// <remarks>
/// <para>
/// The rule that matters most: an adapter already on another network is never chosen
/// automatically. On 2026-10-02 an early build picked "the first idle adapter", which on the
/// owner's laptop was the built-in one moments before it rejoined their phone's hotspot, and
/// Windows itself also joined the camera on that adapter from a saved profile. Either way the PC
/// lost its Wi-Fi internet. So taking an adapter off a network is only ever the person's choice,
/// made after reading exactly what it will cost them.
/// </para>
/// <para>
/// Among free adapters, a plugged-in one is preferred to a built-in one: a USB dongle is almost
/// always the spare, and the built-in adapter is almost always the one the PC normally uses. The
/// adapter that worked last time beats both, because the camera's DHCP server remembers it.
/// </para>
/// </remarks>
public static class AdapterAdvisor
{
    /// <summary>Assesses every adapter for joining <paramref name="cameraSsid"/>.</summary>
    /// <param name="adapters">This PC's Wi-Fi adapters.</param>
    /// <param name="cameraSsid">The camera's network name.</param>
    /// <param name="internetElsewhere">
    /// For an adapter, the name of another connection that would keep this PC online if it were
    /// used — Ethernet, USB tethering, another Wi-Fi adapter — or null when there is none.
    /// </param>
    public static IReadOnlyList<AdapterChoice> Assess(
        IReadOnlyList<WifiAdapter> adapters, string cameraSsid, Func<WifiAdapter, string?> internetElsewhere)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraSsid);
        ArgumentNullException.ThrowIfNull(internetElsewhere);

        return adapters.Select(adapter => AssessOne(adapter, cameraSsid, internetElsewhere(adapter))).ToList();
    }

    /// <summary>
    /// The adapter to offer first, or null when every adapter would have to leave a network.
    /// </summary>
    /// <remarks>
    /// Never returns a <see cref="AdapterVerdict.LeavesNetwork"/> choice: with only such adapters the
    /// person picks one themselves, having been told what it costs.
    /// </remarks>
    /// <param name="choices">From <see cref="Assess"/>.</param>
    /// <param name="remembered">The adapter that last reached the camera, if any.</param>
    public static AdapterChoice? Recommend(IReadOnlyList<AdapterChoice> choices, Guid? remembered) =>
        Rank(choices, remembered).FirstOrDefault(choice => choice.Verdict != AdapterVerdict.LeavesNetwork);

    /// <summary>
    /// Every choice, best first: already on the camera, then free adapters (the one that worked last
    /// time, then plug-in before built-in), then adapters that would have to leave a network, those
    /// that keep this PC online before those that would not.
    /// </summary>
    /// <remarks>The one ordering every picker and the command line use, so they cannot disagree.</remarks>
    /// <param name="choices">From <see cref="Assess"/>.</param>
    /// <param name="remembered">The adapter that last reached the camera, if any.</param>
    public static IReadOnlyList<AdapterChoice> Rank(IReadOnlyList<AdapterChoice> choices, Guid? remembered)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return choices
            .OrderBy(choice => choice.Verdict)
            .ThenBy(choice => choice.StaysOnline ? 0 : 1)
            .ThenBy(choice => choice.Adapter.Id == remembered ? 0 : 1)
            .ThenBy(choice => choice.Adapter.IsExternal ? 0 : 1)
            .ThenBy(choice => choice.Adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AdapterChoice AssessOne(WifiAdapter adapter, string cameraSsid, string? elsewhere)
    {
        if (adapter.IsOn(cameraSsid))
        {
            return new AdapterChoice(adapter, AdapterVerdict.OnCamera, StaysOnline: true,
                $"{adapter.Name} is already on {cameraSsid}.");
        }

        if (adapter.Connection is not { } current)
        {
            return new AdapterChoice(adapter, AdapterVerdict.Free, StaysOnline: true,
                $"{adapter.Name} is free. Nothing else on this PC changes.");
        }

        return elsewhere is null
            ? new AdapterChoice(adapter, AdapterVerdict.LeavesNetwork, StaysOnline: false,
                $"{adapter.Name} would leave {current.Ssid}, and this PC would be offline until you disconnect from the camera. Sightline puts it back on {current.Ssid} when you do.")
            : new AdapterChoice(adapter, AdapterVerdict.LeavesNetwork, StaysOnline: true,
                $"{adapter.Name} would leave {current.Ssid}. This PC stays online through {elsewhere}, and Sightline puts {adapter.Name} back on {current.Ssid} when you disconnect.");
    }
}
