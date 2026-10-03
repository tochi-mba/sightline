using Sightline.Protocol.GpSock;

namespace Sightline.App.ViewModels;

/// <summary>One card file, formatted for the Windows picker.</summary>
public sealed class CameraFileViewModel
{
    /// <summary>Creates a row from the camera's own entry.</summary>
    public CameraFileViewModel(CameraFile file) => File = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>The protocol value used by download and delete.</summary>
    public CameraFile File { get; }

    /// <summary>The best name available before its bytes reveal the extension.</summary>
    public string Name => File.DisplayName;

    /// <summary>Photo, Video, protected video, or another camera code.</summary>
    public string Kind => File.Kind.ToString();

    /// <summary>The camera-clock time, clearly unknown when it was invalid.</summary>
    public string Taken => File.Taken?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Time unknown";

    /// <summary>A compact approximate size.</summary>
    public string Size => File.ApproximateBytes >= 1024 * 1024
        ? $"{File.ApproximateBytes / (1024d * 1024d):0.0} MB"
        : $"{File.ApproximateBytes / 1024d:0} KB";
}
