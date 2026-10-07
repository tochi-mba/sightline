using System.Collections.Immutable;
using Sightline.Protocol.GpSock;

namespace Sightline.Core.Camera;

/// <summary>Where the connection to the camera is.</summary>
public abstract record Connection
{
    private Connection()
    {
    }

    /// <summary>Not connected, and not trying.</summary>
    public sealed record Idle : Connection
    {
        /// <summary>The one value.</summary>
        public static Idle Instance { get; } = new();
    }

    /// <summary>Joining the camera's Wi-Fi.</summary>
    public sealed record Joining : Connection
    {
        /// <summary>The one value.</summary>
        public static Joining Instance { get; } = new();
    }

    /// <summary>On the camera's network, opening the control channel and reading the camera.</summary>
    public sealed record Opening : Connection
    {
        /// <summary>The one value.</summary>
        public static Opening Instance { get; } = new();
    }

    /// <summary>Connected; commands are accepted.</summary>
    public sealed record Connected : Connection
    {
        /// <summary>The one value.</summary>
        public static Connected Instance { get; } = new();
    }

    /// <summary>
    /// The camera was lost, and this is attempt <paramref name="Attempt"/> of at most <paramref name="Of"/> to get it
    /// back.
    /// </summary>
    public sealed record Reconnecting(int Attempt, int Of, Problem Problem) : Connection;

    /// <summary>Not connected, because of <paramref name="Problem"/>.</summary>
    public sealed record Failed(Problem Problem) : Connection;
}

/// <summary>What the shutter does. The camera is switched to match, so its own screen agrees.</summary>
public enum CaptureMode
{
    /// <summary>Recording video to the card.</summary>
    Video,

    /// <summary>Taking photos onto the card.</summary>
    Photo,
}

/// <summary>The camera mode each capture mode needs.</summary>
public static class CaptureModes
{
    /// <summary>The camera's own mode for <paramref name="mode"/>.</summary>
    public static CameraMode CameraMode(this CaptureMode mode) =>
        mode == CaptureMode.Video ? Protocol.GpSock.CameraMode.Record : Protocol.GpSock.CameraMode.Capture;
}

/// <summary>The live picture's state. The pictures themselves arrive through <see cref="CameraController.FrameArrived"/>.</summary>
public abstract record LiveView
{
    private LiveView()
    {
    }

    /// <summary>Nobody is watching, or there is no camera.</summary>
    public sealed record Off : LiveView
    {
        /// <summary>The one value.</summary>
        public static Off Instance { get; } = new();
    }

    /// <summary>Starting the stream.</summary>
    public sealed record Starting : LiveView
    {
        /// <summary>The one value.</summary>
        public static Starting Instance { get; } = new();
    }

    /// <summary>Pictures are arriving, <paramref name="FramesPerSecond"/> of them a second over the last second.</summary>
    public sealed record Playing(double FramesPerSecond) : LiveView;

    /// <summary>Stopped while the camera's card is read, which the camera cannot do while streaming.</summary>
    public sealed record Paused : LiveView
    {
        /// <summary>The one value.</summary>
        public static Paused Instance { get; } = new();
    }

    /// <summary>The stream failed for <paramref name="Reason"/>; it is started again shortly.</summary>
    public sealed record Interrupted(string Reason) : LiveView;
}

/// <summary>One picture from the live view. <paramref name="Number"/> counts up, so the same bytes twice are still two frames.</summary>
public sealed record LiveFrame(byte[] Jpeg, int Width, int Height, long Number);

/// <summary>
/// What the camera last reported about itself, in the terms the app shows: only the fields the status
/// decoder has pinned down on the reference camera. Its battery level is not one of them.
/// </summary>
public sealed record CameraStatus(
    CameraMode? Mode,
    bool IsRecording,
    bool OnExternalPower,
    TimeSpan? ClipLength,
    TimeSpan? RecordTimeLeft,
    int? PhotosLeft)
{
    /// <summary>The app's view of a status the camera sent.</summary>
    public static CameraStatus Of(DeviceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new(
            Enum.IsDefined(status.Mode) ? status.Mode : null,
            status.IsRecording,
            status.OnExternalPower,
            status.ClipLength,
            status.RecordTimeLeft,
            status.PhotosLeft);
    }
}

/// <summary>One of the camera's settings and what it is set to now.</summary>
/// <param name="Menu">The setting as the camera's own menu describes it.</param>
/// <param name="Value">The choice it is set to, read back from the camera; null for one that is not a choice or did not answer.</param>
/// <param name="Text">Its text, for a text or read-only setting the camera reported.</param>
public sealed record CameraSetting(MenuSetting Menu, int? Value, string? Text)
{
    /// <summary>The camera's name for the current choice, its text, or null when neither is known.</summary>
    public string? Shown => Value is { } value ? Menu.LabelFor(value) : Text;

    /// <summary>
    /// Whether the app offers to change it: choices only. Text settings, the Wi-Fi name and password,
    /// have a format on the wire not proven on the reference camera, and a password written wrongly would
    /// lock a person out of their camera; actions are left to the camera's own menu.
    /// </summary>
    public bool IsChangeable => Menu.Kind == MenuSettingKind.Choice && Menu.Choices.Count > 0;
}

/// <summary>One file being copied off the card.</summary>
public abstract record Transfer
{
    private Transfer()
    {
    }

    /// <summary>Waiting for the files before it.</summary>
    public sealed record Queued : Transfer
    {
        /// <summary>The one value.</summary>
        public static Queued Instance { get; } = new();
    }

    /// <summary><paramref name="Copied"/> bytes so far of about <paramref name="Expected"/>.</summary>
    public sealed record Copying(long Copied, long Expected) : Transfer;

    /// <summary>Saved, to <paramref name="Where"/>.</summary>
    public sealed record Saved(string Where) : Transfer;

    /// <summary>Not saved, because of <paramref name="Reason"/>.</summary>
    public sealed record Failed(string Reason) : Transfer;
}

/// <summary>What is on the camera's card.</summary>
/// <param name="Files">Every file; null until the card has been read.</param>
/// <param name="Thumbnails">Each file's thumbnail JPEG, once fetched.</param>
/// <param name="Reading">Whether the card is being read now.</param>
/// <param name="Transfers">Each file being, or that has been, copied in this session.</param>
public sealed record Library(
    ImmutableList<CameraFile>? Files,
    ImmutableDictionary<CameraFile, byte[]> Thumbnails,
    bool Reading,
    ImmutableDictionary<CameraFile, Transfer> Transfers)
{
    /// <summary>A card not read yet.</summary>
    public static Library Unread { get; } = new(null, ImmutableDictionary<CameraFile, byte[]>.Empty, false, ImmutableDictionary<CameraFile, Transfer>.Empty);
}

/// <summary>What the camera is doing for the person right now; the controls that would clash wait for it.</summary>
public enum CameraTask
{
    /// <summary>Taking a photo.</summary>
    TakingPhoto,

    /// <summary>Starting a recording.</summary>
    StartingRecording,

    /// <summary>Stopping a recording.</summary>
    StoppingRecording,

    /// <summary>Switching between video and photos.</summary>
    SwitchingMode,

    /// <summary>Changing one of the camera's settings.</summary>
    ChangingSetting,

    /// <summary>Reading the card.</summary>
    ReadingCard,

    /// <summary>Copying from the card.</summary>
    Copying,

    /// <summary>Deleting from the card.</summary>
    Deleting,

    /// <summary>Fetching a clip from the card to play it.</summary>
    Playing,
}

/// <summary>A message for the person about something that just happened.</summary>
/// <param name="Id">Counts up, so the same message twice is still shown twice, and dismissing one cannot dismiss a newer one.</param>
/// <param name="Text">What to say.</param>
public sealed record Notice(long Id, string Text);

/// <summary>Everything the window shows about the camera.</summary>
public sealed record CameraState(
    Connection Connection,
    string? CameraName,
    CameraStatus? Status,
    CaptureMode Mode,
    LiveView Live,
    ImmutableList<CameraSetting> Settings,
    Library Library,
    CameraTask? Task,
    Notice? Notice)
{
    /// <summary>Not connected, nothing known.</summary>
    public static CameraState Initial { get; } = new(
        Connection.Idle.Instance, null, null, CaptureMode.Video, LiveView.Off.Instance, [], Library.Unread, null, null);

    /// <summary>Whether commands are accepted.</summary>
    public bool IsConnected => Connection is Connection.Connected;

    /// <summary>Whether the camera is recording to its card.</summary>
    public bool IsRecording => Status?.IsRecording == true;
}
