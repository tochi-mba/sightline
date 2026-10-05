using Sightline.Testing;

namespace Sightline.Core.Testing;

/// <summary>The reference camera, as the fakes describe it.</summary>
public static class ReferenceCamera
{
    /// <summary>The reference camera's own menu XML, every setting it has.</summary>
    public static string MenuXml { get; } = Read();

    /// <summary>A fake camera that describes itself with the reference camera's menu.</summary>
    public static FakeCamera Fake() => new() { MenuXml = MenuXml };

    private static string Read()
    {
        using var stream = typeof(ReferenceCamera).Assembly.GetManifestResourceStream("reference-camera.xml")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
