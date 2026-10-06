using System.Globalization;

namespace Sightline.Core.Camera;

/// <summary>
/// What the window says about the camera, worked out from its state: the same words the Android app uses,
/// so the two read alike.
/// </summary>
public static class CameraWords
{
    /// <summary>One word on the connection, for the heading's pill, or null when there is nothing to say.</summary>
    public static string? Connection(Connection connection) => connection switch
    {
        Camera.Connection.Joining or Camera.Connection.Opening => "Connecting",
        Camera.Connection.Connected => "Connected",
        Camera.Connection.Reconnecting => "Reconnecting",
        Camera.Connection.Failed => "Not connected",
        _ => null,
    };

    /// <summary>Why the picture is not simply playing, in words, or null when it is.</summary>
    public static string? Picture(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return (camera.Connection, camera.Live) switch
        {
            (Camera.Connection.Reconnecting r, _) => $"Reconnecting to the camera, attempt {r.Attempt} of {r.Of}",
            (_, LiveView.Interrupted) => "The picture stopped. Starting it again.",
            (_, LiveView.Starting) => "Starting the picture",
            (_, LiveView.Paused) => "Paused while the card is read",
            _ => null,
        };
    }

    /// <summary>What the shutter does now.</summary>
    public static string Shutter(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.IsRecording ? "Stop recording"
            : camera.Mode == CaptureMode.Video ? "Start recording"
            : "Take a photo";
    }

    /// <summary>
    /// The line above the controls: what the camera is doing for the person, or what its card still holds
    /// in the current mode, and whether it is on USB power.
    /// </summary>
    public static string Status(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (camera.Task is { } task)
        {
            return Task(task);
        }

        if (camera.Status is not { } status)
        {
            return "";
        }

        var room = status.IsRecording ? "Recording to the camera's card"
            : camera.Mode == CaptureMode.Photo ? (status.PhotosLeft is { } photos ? $"Room for {photos} more photos" : null)
            : status.RecordTimeLeft is { } left ? $"Room for {Clock(left)} more video"
            : null;
        var power = status.OnExternalPower ? "On USB power" : null;
        return string.Join("  ·  ", new[] { room, power }.OfType<string>());
    }

    /// <summary>What the camera is busy with, for the person who asked.</summary>
    public static string Task(CameraTask task) => task switch
    {
        CameraTask.TakingPhoto => "Taking a photo",
        CameraTask.StartingRecording => "Starting to record",
        CameraTask.StoppingRecording => "Stopping the recording",
        CameraTask.SwitchingMode => "Switching mode",
        CameraTask.ChangingSetting => "Changing a setting",
        CameraTask.ReadingCard => "Reading the card",
        CameraTask.Copying => "Copying from the card",
        _ => "Deleting from the card",
    };

    /// <summary>A length as a clock shows it: 1:23, or 1:02:05 past an hour.</summary>
    public static string Clock(TimeSpan length)
    {
        var total = (long)length.TotalSeconds;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = (total % 60).ToString("00", CultureInfo.InvariantCulture);
        return hours > 0
            ? $"{hours}:{minutes.ToString("00", CultureInfo.InvariantCulture)}:{seconds}"
            : $"{minutes}:{seconds}";
    }
}
