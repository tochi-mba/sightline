namespace Sightline.Core.Settings;

/// <summary>Where Sightline keeps what it remembers for the person using this PC.</summary>
/// <remarks>
/// Apart from the install folder, %LOCALAPPDATA%\Sightline, which belongs to the installer: uninstalling
/// deletes it whole, and settings kept there would go with it, unasked. Copies of the camera's photos and
/// videos go to the person's Downloads, not here.
/// </remarks>
public static class DataFolder
{
    /// <summary>%LOCALAPPDATA%\REX Technologies\Sightline.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "REX Technologies", "Sightline");
}
