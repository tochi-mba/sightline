using BenchmarkDotNet.Attributes;
using Sightline.Benchmarks.Camera;
using Sightline.Protocol.GpSock;

namespace Sightline.Benchmarks;

/// <summary>
/// Reading what the camera says it has: its settings menu, and a page of the files on its card.
/// </summary>
/// <remarks>
/// The menu is parsed on every connect before the settings screen can show anything, and this is
/// the reference camera's own 18,523-byte document. A page of the file list is parsed for every page
/// of the card library.
/// </remarks>
public class CatalogueParsing
{
    private byte[] menu = [];
    private byte[] page = [];

    [GlobalSetup]
    public void Setup()
    {
        menu = ReferenceCamera.Menu();

        // The payload of a full page, without its acknowledgement's header: what ParsePage is given.
        page = Answers.FileListPages(Answers.FilesPerPage)[0][GpSockFrame.ResponseHeaderLength..];

        Expect.Equal(21, ParseMenu().Settings.Count, "settings in the reference menu");
        Expect.Equal(Answers.FilesPerPage, ParseFileListPage().Count, "files on a page");
    }

    /// <summary>The reference camera's whole menu, from bytes to settings.</summary>
    [Benchmark]
    public MenuCatalog ParseMenu() => MenuCatalog.Parse(menu);

    /// <summary>One full page of the card's file list.</summary>
    [Benchmark]
    public IReadOnlyList<CameraFile> ParseFileListPage() => CameraFile.ParsePage(page);
}
