using System.Text;

namespace Sightline.Benchmarks.Camera;

/// <summary>
/// What the reference camera itself sent, from the sanitised vectors in protocol/golden.
/// </summary>
internal static class ReferenceCamera
{
    /// <summary>The camera's whole settings menu: 18,523 bytes of XML, 21 settings in 4 categories.</summary>
    public static byte[] Menu() => Resource("reference-camera.xml");

    /// <summary>Its 16-byte answer to GetDeviceStatus.</summary>
    public static byte[] Status() =>
        Convert.FromHexString(Encoding.ASCII.GetString(Resource("device-status-16byte.hex")).Trim());

    private static byte[] Resource(string name)
    {
        // The project file embeds both, so a missing one is a broken build and fails here, before
        // anything is measured.
        using var stream = typeof(ReferenceCamera).Assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
