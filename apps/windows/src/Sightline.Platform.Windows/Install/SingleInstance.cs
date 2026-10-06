namespace Sightline.Platform.Windows.Install;

/// <summary>
/// Keeps one Sightline running per person: a second start shows the first one's window and leaves, and
/// the installer asks the running one to quit before it replaces it.
/// </summary>
/// <remarks>
/// Two copies would fight over the camera, which answers one client at a time, and over the Wi-Fi adapter.
/// The named objects live in the session's own namespace, so another person signed in to the same PC runs
/// their own copy.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name every Sightline claims.</summary>
    public const string SightlineName = "REXTechnologies.Sightline";

    private readonly Mutex claim;
    private readonly EventWaitHandle wake;
    private readonly EventWaitHandle quit;
    private RegisteredWaitHandle? listeningForWake;
    private RegisteredWaitHandle? listeningForQuit;

    /// <summary>Claims <paramref name="name"/> for this copy, unless a copy running already has it.</summary>
    public SingleInstance(string name = SightlineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Not owned: only whether it was created matters, so nothing ties it to the thread that made it.
        claim = new Mutex(initiallyOwned: false, MutexName(name), out var created);
        IsFirst = created;
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Quit");
    }

    /// <summary>Whether this is the only copy running, and so the one to carry on.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// Asks the copy running under <paramref name="name"/>, if any, to quit, and waits up to
    /// <paramref name="wait"/> for it to have gone.
    /// </summary>
    /// <returns>Whether no copy is running any more.</returns>
    public static bool AskToQuit(string name, TimeSpan wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Quit"))
        {
            quit.Set();
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            using (new Mutex(initiallyOwned: false, MutexName(name), out var free))
            {
                if (free)
                {
                    return true;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>Asks the copy that is running to show its window.</summary>
    public void WakeFirst() => wake.Set();

    /// <summary>Runs <paramref name="show"/>, on a pool thread, each time another start asks.</summary>
    public void OnWake(Action show) => Listen(wake, show, ref listeningForWake);

    /// <summary>Runs <paramref name="leave"/>, on a pool thread, when the installer asks this copy to quit.</summary>
    public void OnQuit(Action leave) => Listen(quit, leave, ref listeningForQuit);

    /// <inheritdoc />
    public void Dispose()
    {
        listeningForWake?.Unregister(null);
        listeningForQuit?.Unregister(null);
        wake.Dispose();
        quit.Dispose();
        claim.Dispose();
    }

    private static string MutexName(string name) => $@"Local\{name}";

    private static void Listen(EventWaitHandle signal, Action action, ref RegisteredWaitHandle? listening)
    {
        ArgumentNullException.ThrowIfNull(action);
        listening?.Unregister(null);
        listening = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);
    }
}
