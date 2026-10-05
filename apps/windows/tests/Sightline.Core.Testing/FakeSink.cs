using Sightline.Core.Camera;
using Sightline.Protocol.GpSock;

namespace Sightline.Core.Testing;

/// <summary>A gallery in memory that can be told to fail.</summary>
public sealed class FakeSink : IMediaSink
{
    public List<(CameraFile File, MediaKind Kind)> Created { get; } = [];

    public Dictionary<string, byte[]> Published { get; } = [];

    public List<CameraFile> Discarded { get; } = [];

    /// <summary>When set, creating a file fails as a full disk does.</summary>
    public bool Full { get; set; }

    /// <summary>When set, the last step, giving the file its name, fails.</summary>
    public bool PublishFails { get; set; }

    /// <summary>When set, Windows denies the last step, as it does a folder the person may not write to.</summary>
    public bool PublishDenied { get; set; }

    /// <summary>When set, throwing a partial file away fails too.</summary>
    public bool DiscardFails { get; set; }

    /// <summary>Runs as each file is created: the moment a copy is known to be under way.</summary>
    public Action OnCreate { get; set; } = () => { };

    public IPendingMedia Create(CameraFile file, MediaKind kind)
    {
        if (Full)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        OnCreate();
        Created.Add((file, kind));
        return new Pending(this, file, kind);
    }

    private sealed class Pending(FakeSink sink, CameraFile file, MediaKind kind) : IPendingMedia, IDisposable
    {
        private readonly MemoryStream bytes = new();

        public Stream Output => bytes;

        public string Publish()
        {
            if (sink.PublishFails)
            {
                throw new IOException("The file is in use by another process.");
            }

            if (sink.PublishDenied)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }

            var name = $"Downloads/{file.DisplayName}{kind.Extension()}";
            sink.Published[name] = bytes.ToArray();
            return name;
        }

        public void Discard()
        {
            sink.Discarded.Add(file);
            if (sink.DiscardFails)
            {
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }

        public void Dispose() => bytes.Dispose();
    }
}
