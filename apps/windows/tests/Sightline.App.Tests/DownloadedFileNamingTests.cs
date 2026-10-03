using Shouldly;
using Sightline.App.Services;
using Xunit;

namespace Sightline.App.Tests;

public sealed class DownloadedFileNamingTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"sightline-test-{Guid.NewGuid():N}");

    public DownloadedFileNamingTests() => Directory.CreateDirectory(folder);

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, ".jpg")]
    [InlineData(new byte[] { 0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, ".mp4")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'A', (byte)'V', (byte)'I', (byte)' ' }, ".avi")]
    [InlineData(new byte[] { 1, 2 }, ".bin")]
    public void Extension_comes_from_the_bytes(byte[] bytes, string expected)
    {
        var path = Path.Combine(folder, "file");
        File.WriteAllBytes(path, bytes);

        DownloadedFileNaming.ExtensionFor(path).ShouldBe(expected);
    }

    [Fact]
    public void Available_path_never_replaces_an_existing_download()
    {
        File.WriteAllText(Path.Combine(folder, "MOVI0001.avi"), "one");
        File.WriteAllText(Path.Combine(folder, "MOVI0001-2.avi"), "two");

        DownloadedFileNaming.AvailablePath(folder, "MOVI0001", ".avi")
            .ShouldBe(Path.Combine(folder, "MOVI0001-3.avi"));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
