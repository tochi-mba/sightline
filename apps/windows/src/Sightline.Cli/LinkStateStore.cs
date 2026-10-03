using System.Text.Json;
using System.Text.Json.Serialization;
using Sightline.Core.Connectivity;

namespace Sightline.Cli;

/// <summary>
/// What <c>sightline connect</c> changed, kept on disk until <c>sightline disconnect</c> undoes it.
/// </summary>
/// <remarks>
/// The two commands are separate processes. Without this, disconnect could not know which adapter
/// to put back on which network, and the honest fallback — doing nothing — would leave somebody's
/// built-in Wi-Fi on the camera.
/// </remarks>
internal sealed class LinkStateStore(string path)
{
    /// <summary>The store in the current user's local application data.</summary>
    public static LinkStateStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sightline", "cli-link.json"));

    /// <summary>The saved state, or null when there is none or it cannot be read.</summary>
    public LinkState? Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), LinkStateJson.Default.LinkState);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves <paramref name="state"/>, replacing any earlier one.</summary>
    public void Save(LinkState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, LinkStateJson.Default.LinkState));
    }

    /// <summary>Forgets the saved state.</summary>
    public void Clear() => File.Delete(path);
}

/// <summary>Generated serialisation, so the command needs no reflection to read its own file.</summary>
[JsonSerializable(typeof(LinkState))]
internal sealed partial class LinkStateJson : JsonSerializerContext;
