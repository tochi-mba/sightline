using Velopack;
using Velopack.Sources;

namespace Sightline.Platform.Windows.Install;

/// <summary>Where a newer Sightline comes from, and how it is applied.</summary>
/// <remarks>
/// An interface so the update flow is tested without a network, an installed app or a restart, none of
/// which a test may do.
/// </remarks>
public interface IUpdateSource
{
    /// <summary>
    /// Whether this copy was installed, rather than run as the portable exe. A portable copy has no
    /// installer to hand a new version to, so it is never offered one; it is sent to the download instead.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>The newer version on offer, or null when this build is the newest.</summary>
    Task<string?> CheckForNewVersionAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the version the last check found.</summary>
    Task DownloadAsync(CancellationToken cancellationToken);

    /// <summary>Applies the downloaded version and restarts Sightline.</summary>
    void ApplyAndRestart();
}

/// <summary>The real source: this repository's GitHub releases, through Velopack.</summary>
public sealed class VelopackUpdateSource : IUpdateSource
{
    /// <summary>The repository the installer and its updates are published from.</summary>
    public const string RepositoryUrl = "https://github.com/tochi-mba/sightline";

    private readonly UpdateManager manager;
    private UpdateInfo? pending;

    /// <summary>Creates a source over this installation.</summary>
    public VelopackUpdateSource()
    {
        // A rolling install follows rolling builds and a tagged install follows tags, read off the installed
        // version, so somebody who chose a stable release is never moved onto a build that changes under them.
        var installed = new UpdateManager(RepositoryUrl);
        var prerelease = installed.IsInstalled && (installed.CurrentVersion?.IsPrerelease ?? false);
        manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: prerelease));
    }

    /// <inheritdoc />
    public bool IsInstalled => manager.IsInstalled;

    /// <inheritdoc />
    public async Task<string?> CheckForNewVersionAsync(CancellationToken cancellationToken)
    {
        pending = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return pending?.TargetFullRelease.Version.ToString();
    }

    /// <inheritdoc />
    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (pending is not null)
        {
            await manager.DownloadUpdatesAsync(pending, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void ApplyAndRestart()
    {
        if (pending is not null)
        {
            manager.ApplyUpdatesAndRestart(pending);
        }
    }
}
