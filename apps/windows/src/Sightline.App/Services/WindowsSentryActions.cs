using System.Globalization;
using Sightline.Core.Sentry;

namespace Sightline.App.Services;

/// <summary>An alarm, as the window and the tray show it.</summary>
/// <param name="At">When it went up.</param>
/// <param name="SavedTo">Where its picture was saved, or null when it was not.</param>
public sealed record AlarmNotice(DateTimeOffset At, string? SavedTo);

/// <summary>
/// What an alarm does on this PC: the picture that raised it saved to a folder when the setting says so,
/// and the window and tray told.
/// </summary>
public sealed class WindowsSentryActions : ISentryActions
{
    private readonly string folder;
    private readonly TimeZoneInfo zone;

    /// <summary>Saves pictures into <paramref name="folder"/>, named by the time in <paramref name="zone"/>.</summary>
    public WindowsSentryActions(string folder, TimeZoneInfo? zone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        this.folder = folder;
        this.zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>Where Sentry's pictures go: Pictures\Sightline\Sentry.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sightline", "Sentry");

    /// <summary>Where alarm pictures are saved.</summary>
    public string Folder => folder;

    /// <summary>Raised for each alarm, from Sentry's thread.</summary>
    public event Action<AlarmNotice>? Raised;

    /// <inheritdoc />
    public void AlarmRaised(DateTimeOffset at, byte[] snapshot, bool saveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var saved = saveSnapshot ? Save(at, snapshot) : null;
        Raised?.Invoke(new AlarmNotice(at, saved));
    }

    /// <inheritdoc />
    public void AlarmEnded()
    {
        // The notice stays, so an alarm that came and went while nobody was looking is still there to see.
    }

    /// <summary>Saves the picture, or returns null when the disk will not take it: an alarm still tells.</summary>
    private string? Save(DateTimeOffset at, byte[] snapshot)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var local = TimeZoneInfo.ConvertTime(at, zone);
            var name = "sentry-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jpg";
            var path = Core.Camera.FolderSink.AvailablePath(folder, name);
            File.WriteAllBytes(path, snapshot);
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
