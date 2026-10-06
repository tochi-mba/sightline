using Avalonia.Media.Imaging;
using Sightline.App.Services;
using Sightline.App.ViewModels;
using Sightline.Core;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Core.Sentry;
using Sightline.Core.Settings;
using Sightline.Core.Testing;
using Sightline.Core.Updates;
using Sightline.Testing;
using SkiaSharp;

namespace Sightline.App.Tests.ViewModels;

/// <summary>
/// The window's parts over the real controller and the shared fakes: a fake camera on a fake network, fake
/// Wi-Fi, preferences in a temporary folder, and real Sentry. Pictures decode as they do in the app.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    /// <summary>The controller's timings, shortened so a test of a stall or a reconnect takes milliseconds.</summary>
    public static readonly ControllerTiming Quick = new(
        Answer: TimeSpan.FromMilliseconds(400),
        LongAnswer: TimeSpan.FromSeconds(3),
        TransferStall: TimeSpan.FromMilliseconds(400),
        StatusInterval: TimeSpan.FromMilliseconds(100),
        LiveRetry: TimeSpan.FromMilliseconds(50),
        LiveRetryCap: TimeSpan.FromMilliseconds(150),
        ReconnectDelay: TimeSpan.FromMilliseconds(50),
        ReconnectAttempts: 3);

    private static readonly CameraSessionTiming QuickStream = new(
        TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200));

    private readonly Lock gate = new();
    private int posted;

    /// <param name="preferences">The preferences to start from; the defaults when omitted.</param>
    /// <param name="notes">What's new in each version; the curated notes when omitted.</param>
    /// <param name="post">How work reaches the window's thread; at once, one at a time, when omitted.</param>
    public TestApp(Preferences? preferences = null, IReadOnlyList<WhatsNewEntry>? notes = null, Action<Action>? post = null)
    {
        Directory.CreateDirectory(Folder);
        Preferences = new PreferencesStore(Path.Combine(Folder, "preferences.json"));
        if (preferences is not null)
        {
            Preferences.Update(_ => preferences);
        }

        Link = new FakeLink(ReferenceCamera.Fake()) { Stream = s => s.Frames.Add(TestPictures.Jpeg(90)) };
        Controller = new CameraController(Link, Quick, QuickStream, () => Preferences.Current.Reconnect);
        Alarms = new WindowsSentryActions(Path.Combine(Folder, "sentry"));
        Sentry = new SentryRunner(Controller, () => Preferences.Current.Sentry, Alarms, LumaSampler.Grid);
        Parts = new AppParts(
            Controller, Choice, Preferences, new CameraFinder(Wlan, Network), Sentry, Alarms, Desktop,
            Decode, post ?? Post, "0.2.0", Path.Combine(Folder, "snapshots"), notes);
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "sightline-app-" + Guid.NewGuid().ToString("N"));

    public FakeLink Link { get; }

    public FakeCamera Control => Link.Control;

    public CameraController Controller { get; }

    public PreferencesStore Preferences { get; }

    public CameraChoice Choice { get; } = new();

    public FakeWlan Wlan { get; } = new();

    public FakeNetwork Network { get; } = new();

    public WindowsSentryActions Alarms { get; }

    public SentryRunner Sentry { get; }

    public RecordedDesktop Desktop { get; } = new();

    public AppParts Parts { get; private set; }

    /// <summary>These parts with <paramref name="updates"/> as where newer versions come from.</summary>
    public TestApp WithUpdates(Sightline.Platform.Windows.Install.IUpdateSource updates)
    {
        Parts = Parts with { Updates = updates };
        return this;
    }

    /// <summary>How many actions were posted to the window's thread.</summary>
    public int Posted => Volatile.Read(ref posted);

    /// <summary>Decodes as the app does: a picture, or null for bytes that are not one.</summary>
    public static Bitmap? Decode(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, looking again every few milliseconds.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Connects and waits until connected.</summary>
    public async Task ConnectedAsync()
    {
        _ = Controller.Connect();
        await EventuallyAsync(() => Controller.State.IsConnected);
    }

    public async ValueTask DisposeAsync()
    {
        Sentry.Dispose();
        await Controller.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A picture still open somewhere; the temporary folder goes with the next clean.
        }
    }

    /// <summary>Runs posted actions at once, one at a time, as the window's one thread would.</summary>
    private void Post(Action action)
    {
        Interlocked.Increment(ref posted);
        lock (gate)
        {
            action();
        }
    }
}

/// <summary>Windows' folders and browser, recording what was asked of them.</summary>
internal sealed class RecordedDesktop : IDesktop
{
    public List<string> Folders { get; } = [];

    public List<string> Links { get; } = [];

    public void OpenFolder(string path) => Folders.Add(path);

    public void OpenLink(string url) => Links.Add(url);
}

/// <summary>Real JPEGs, which the app's decoder accepts.</summary>
internal static class TestPictures
{
    /// <summary>A small picture of one shade of grey.</summary>
    public static byte[] Jpeg(byte shade, int width = 64, int height = 36)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(shade, shade, shade));
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>
    /// A grey scene with a bright block on the left or the right: what someone crossing the picture looks
    /// like to Sentry, which ignores the whole picture brightening, as an exposure change does.
    /// </summary>
    public static byte[] Moving(bool left)
    {
        using var bitmap = new SKBitmap(320, 180);
        bitmap.Erase(new SKColor(60, 60, 60));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.White })
        {
            canvas.DrawRect(left ? SKRect.Create(20, 20, 120, 140) : SKRect.Create(180, 20, 120, 140), paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
