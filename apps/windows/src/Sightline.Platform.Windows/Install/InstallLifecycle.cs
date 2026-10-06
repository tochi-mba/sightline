namespace Sightline.Platform.Windows.Install;

/// <summary>What the installer asks this build to do when it is installed, updated or removed.</summary>
/// <remarks>
/// <para>
/// Velopack runs the new executable with a hook argument and expects it to do this one job and exit. The
/// work lives here rather than in <c>Main</c> so it can be tested with a fake PATH and a fake way of asking
/// other copies to quit; <c>Main</c> only wires these to Velopack's callbacks.
/// </para>
/// <para>
/// Every hook first asks any other running copy to quit, through the same channel a second start uses to
/// show the first. A copy asked that way leaves its camera properly, which puts its Wi-Fi adapter back,
/// rather than being killed with the adapter still on the camera.
/// </para>
/// </remarks>
/// <param name="pathStore">The user's PATH.</param>
/// <param name="installDirectory">The folder the installer put this build in.</param>
/// <param name="askOthersToQuit">Asks every other running Sightline to quit, and waits a while for it.</param>
/// <param name="announcePathChange">Tells the desktop the PATH changed, so a new terminal sees it.</param>
public sealed class InstallLifecycle(
    IUserPathStore pathStore,
    string installDirectory,
    Action askOthersToQuit,
    Action announcePathChange)
{
    private readonly IUserPathStore pathStore = pathStore ?? throw new ArgumentNullException(nameof(pathStore));
    private readonly string installDirectory = string.IsNullOrWhiteSpace(installDirectory)
        ? throw new ArgumentException("The install folder is required.", nameof(installDirectory))
        : installDirectory;
    private readonly Action askOthersToQuit = askOthersToQuit ?? throw new ArgumentNullException(nameof(askOthersToQuit));
    private readonly Action announcePathChange = announcePathChange ?? throw new ArgumentNullException(nameof(announcePathChange));

    /// <summary>The lifecycle of the build running now, against the real PATH and the real running copies.</summary>
    public static InstallLifecycle ForThisInstall() => new(
        new RegistryUserPathStore(),
        AppContext.BaseDirectory,
        () => SingleInstance.AskToQuit(SingleInstance.SightlineName, TimeSpan.FromSeconds(15)),
        PathRegistration.AnnounceChange);

    /// <summary>A fresh install: no other copy running, and <c>sightline</c> on the PATH.</summary>
    public void AfterInstall()
    {
        askOthersToQuit();
        if (PathRegistration.Add(pathStore, installDirectory))
        {
            announcePathChange();
        }
    }

    /// <summary>An update in place: the folder is the same, so the PATH already names it.</summary>
    public void AfterUpdate() => askOthersToQuit();

    /// <summary>A removal: nothing left running, and only Sightline's own PATH entry taken away.</summary>
    public void BeforeUninstall()
    {
        askOthersToQuit();
        if (PathRegistration.Remove(pathStore, installDirectory))
        {
            announcePathChange();
        }
    }
}
