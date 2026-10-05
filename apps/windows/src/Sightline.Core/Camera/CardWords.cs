using System.Globalization;
using Sightline.Protocol.GpSock;

namespace Sightline.Core.Camera;

/// <summary>What the window says about the files on the card: the Android app's words, for this PC.</summary>
public static class CardWords
{
    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = Megabyte * 1024;

    /// <summary>Files by the day they were taken, newest day and newest file first; undated ones last.</summary>
    public static IReadOnlyList<(string Day, IReadOnlyList<CameraFile> Files)> ByDay(IReadOnlyList<CameraFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var days = files
            .Where(file => file.Taken is not null)
            .OrderByDescending(file => file.Taken)
            .GroupBy(file => file.Taken!.Value.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture))
            .Select(day => (day.Key, (IReadOnlyList<CameraFile>)day.ToList()))
            .ToList();
        var undated = files.Where(file => file.Taken is null).ToList();
        if (undated.Count > 0)
        {
            days.Add(("Date unknown", undated));
        }

        return days;
    }

    /// <summary>How many videos and photos, as the heading says it.</summary>
    public static string Count(IReadOnlyList<CameraFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return "Nothing on the card";
        }

        var photos = files.Count(file => file.IsPhoto);
        var videos = files.Count(file => file.IsVideo);
        var others = files.Count - photos - videos;
        return string.Join(", ", new[]
        {
            videos > 0 ? Plural(videos, "video") : null,
            photos > 0 ? Plural(photos, "photo") : null,
            others > 0 ? Plural(others, "other file") : null,
        }.OfType<string>());
    }

    /// <summary>What a file is, in a word or two.</summary>
    public static string Kind(CameraFileKind kind) => kind switch
    {
        CameraFileKind.Photo => "Photo",
        CameraFileKind.Video => "Video",
        CameraFileKind.ProtectedVideo => "Locked video",
        CameraFileKind.EmergencyVideo => "Emergency video",
        _ => "File",
    };

    /// <summary>A size as people read it: 295 KB, 9.1 MB, 2.3 GB.</summary>
    public static string Size(long bytes) =>
        bytes < Megabyte ? $"{(bytes + 1023) / 1024} KB"
        : bytes < Gigabyte ? Tenths(bytes, Megabyte) + " MB"
        : Tenths(bytes, Gigabyte) + " GB";

    /// <summary>A file as a screen reader describes it: its kind, when it was taken, its size.</summary>
    public static string Describe(CameraFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var taken = file.Taken?.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return string.Join(", ", new[] { Kind(file.Kind), taken, Size(file.ApproximateBytes) }.OfType<string>());
    }

    /// <summary>Where a copy is, in words.</summary>
    public static string Transfer(Transfer transfer) => transfer switch
    {
        Camera.Transfer.Queued => "Waiting to copy",
        Camera.Transfer.Copying { Expected: > 0 } c => $"Copying, {Math.Min(100, c.Copied * 100 / c.Expected)}%",
        Camera.Transfer.Copying => "Copying",
        Camera.Transfer.Saved => "On this PC",
        Camera.Transfer.Failed f => f.Reason,
        _ => throw new ArgumentNullException(nameof(transfer)),
    };

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Tenths(long bytes, long unit)
    {
        var tenths = ((bytes * 10) + (unit / 2)) / unit;
        return $"{tenths / 10}.{tenths % 10}";
    }
}
