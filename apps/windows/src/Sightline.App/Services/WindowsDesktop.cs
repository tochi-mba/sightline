using System.Diagnostics;
using System.Reflection;
using Avalonia.Media.Imaging;
using Sightline.App.ViewModels;

namespace Sightline.App.Services;

/// <summary>File Explorer and the browser, as the window asks for them.</summary>
public sealed class WindowsDesktop : IDesktop
{
    private readonly Action<ProcessStartInfo> start;

    /// <summary>Starts what is asked for through <paramref name="start"/>; Windows itself when omitted.</summary>
    public WindowsDesktop(Action<ProcessStartInfo>? start = null)
    {
        this.start = start ?? (info => Process.Start(info)?.Dispose());
    }

    /// <inheritdoc />
    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    /// <inheritdoc />
    public void OpenLink(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

/// <summary>Turning the camera's JPEGs into pictures the window can show.</summary>
public static class Pictures
{
    /// <summary>The picture in <paramref name="jpeg"/>, or null when it is not one: one bad frame is not worth an error.</summary>
    public static Bitmap? Decode(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        try
        {
            using var stream = new MemoryStream(jpeg);
            return new Bitmap(stream);
        }
        catch (Exception unreadable) when (unreadable is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>This build's version, as people see it.</summary>
public static class AppVersion
{
    /// <summary>The version of the assembly that holds <paramref name="type"/>, without its source revision.</summary>
    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var informational = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
