namespace Sightline.App.Services;

/// <summary>Names a downloaded card file from its bytes, because the camera list has no extension.</summary>
internal static class DownloadedFileNaming
{
    /// <summary>Recognises the containers this camera family commonly returns, without guessing otherwise.</summary>
    public static string ExtensionFor(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ".jpg";
        }

        if (read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8))
        {
            return ".avi";
        }

        if (read >= 8 && header[4..8].SequenceEqual("ftyp"u8))
        {
            return ".mp4";
        }

        return ".bin";
    }

    /// <summary>Returns a path that does not replace a file the person already has.</summary>
    public static string AvailablePath(string folder, string stem, string extension)
    {
        var candidate = Path.Combine(folder, stem + extension);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(folder, $"{stem}-{suffix}{extension}");
        }

        return candidate;
    }
}
