using Sightline.Protocol.GpSock;

namespace Sightline.Core.Camera;

/// <summary>
/// Saves copies from the card into a folder on this PC.
/// </summary>
/// <remarks>
/// Each file is written under a hidden temporary name and given its real one only once whole, so a copy cut
/// off half-way never leaves a broken photo where a person would open it. A name already taken gets a
/// number rather than replacing what is there: nothing the person already has is ever overwritten.
/// </remarks>
public sealed class FolderSink : IMediaSink
{
    private readonly string folder;

    /// <summary>Saves into <paramref name="folder"/>, which is made when the first file arrives.</summary>
    public FolderSink(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        this.folder = folder;
    }

    /// <summary>Where copies go unless the person chose a folder: Downloads\Sightline.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Sightline");

    /// <summary>The folder copies go to.</summary>
    public string Folder => folder;

    /// <inheritdoc />
    public IPendingMedia Create(CameraFile file, MediaKind kind)
    {
        ArgumentNullException.ThrowIfNull(file);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{file.DisplayName}-{Guid.NewGuid():N}.part");
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
        return new Pending(folder, temporary, file.DisplayName + kind.Extension(), output);
    }

    /// <summary>A name in <paramref name="folder"/> like <paramref name="name"/> that nothing has yet.</summary>
    public static string AvailablePath(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = Path.Combine(folder, name);
        for (var number = 2; File.Exists(candidate); number++)
        {
            candidate = Path.Combine(folder, $"{stem}-{number}{extension}");
        }

        return candidate;
    }

    private sealed class Pending(string folder, string temporary, string name, FileStream output) : IPendingMedia
    {
        public Stream Output => output;

        public string Publish()
        {
            output.Dispose();
            var final = AvailablePath(folder, name);
            File.Move(temporary, final);
            File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden);
            return final;
        }

        public void Discard()
        {
            output.Dispose();
            File.Delete(temporary);
        }
    }
}
