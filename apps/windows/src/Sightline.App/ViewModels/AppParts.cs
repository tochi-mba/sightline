using Avalonia.Media.Imaging;
using Sightline.App.Services;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;
using Sightline.Core.Updates;
using Sightline.Platform.Windows.Install;

namespace Sightline.App.ViewModels;

/// <summary>What the window asks of Windows itself, behind a seam so the pages are tested without it.</summary>
public interface IDesktop
{
    /// <summary>Shows <paramref name="path"/> in File Explorer, making it first when it is missing.</summary>
    void OpenFolder(string path);

    /// <summary>Opens <paramref name="url"/> in the person's browser.</summary>
    void OpenLink(string url);
}

/// <summary>Everything the window is built from, so a test can build it from fakes.</summary>
/// <param name="Controller">The camera.</param>
/// <param name="Choice">The camera the network link joins.</param>
/// <param name="Preferences">The app's settings.</param>
/// <param name="Finder">Finds cameras in range.</param>
/// <param name="Sentry">Sentry, on the controller's live view.</param>
/// <param name="Alarms">What an alarm does on this PC, and its notices.</param>
/// <param name="Desktop">Windows' own folders and browser.</param>
/// <param name="Decode">Turns a JPEG into a picture the window can show, or null when it does not decode.</param>
/// <param name="Post">Runs an action on the window's thread.</param>
/// <param name="Version">This build's version.</param>
/// <param name="SnapshotFolder">Where snapshots of the live picture go.</param>
/// <param name="Notes">What's new in each version; the curated notes when null.</param>
/// <param name="Updates">Where newer versions come from; none, as for a build run from source, when null.</param>
public sealed record AppParts(
    CameraController Controller,
    CameraChoice Choice,
    PreferencesStore Preferences,
    CameraFinder Finder,
    SentryRunner Sentry,
    WindowsSentryActions Alarms,
    IDesktop Desktop,
    Func<byte[], Bitmap?> Decode,
    Action<Action> Post,
    string Version,
    string SnapshotFolder,
    IReadOnlyList<WhatsNewEntry>? Notes = null,
    IUpdateSource? Updates = null);
