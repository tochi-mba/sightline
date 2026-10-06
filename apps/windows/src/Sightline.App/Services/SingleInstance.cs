namespace Sightline.App.Services;

/// <summary>
/// Keeps one Sightline running per person: a second start shows the first one's window and leaves.
/// </summary>
/// <remarks>
/// Two copies would fight over the camera, which answers one client at a time, and over the Wi-Fi adapter.
/// The named objects live in the session's own namespace, so another person signed in to the same PC runs
/// their own copy.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex claim;
    private readonly EventWaitHandle wake;
    private RegisteredWaitHandle? listening;

    /// <summary>Claims <paramref name="name"/> for this copy, unless a copy running already has it.</summary>
    public SingleInstance(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Not owned: only whether it was created matters, so nothing ties it to the thread that made it.
        claim = new Mutex(initiallyOwned: false, $@"Local\{name}", out var created);
        IsFirst = created;
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
    }

    /// <summary>Whether this is the only copy running, and so the one to carry on.</summary>
    public bool IsFirst { get; }

    /// <summary>Asks the copy that is running to show its window.</summary>
    public void WakeFirst() => wake.Set();

    /// <summary>Runs <paramref name="show"/>, on a pool thread, each time another start asks.</summary>
    public void OnWake(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);
        listening?.Unregister(null);
        listening = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        listening?.Unregister(null);
        wake.Dispose();
        claim.Dispose();
    }
}
