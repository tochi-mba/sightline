using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Shouldly;
using Sightline.App.ViewModels;
using Sightline.Core.Playback;
using Sightline.Core.Settings;
using Sightline.Protocol.GpSock;
using Sightline.Testing;

namespace Sightline.App.Tests.ViewModels;

/// <summary>A video from the card, playing while it is still being fetched.</summary>
public sealed class PlayerViewModelTests : IDisposable
{
    private static readonly DateTime Sunday = new(2026, 10, 4, 18, 35, 0);

    private static readonly Preferences Returning = Preferences.Default with { OnboardingDone = true, LastVersion = "0.2.0" };

    private static readonly CameraFile Video = new('A', 7, Sunday, 40);

    private readonly ManualClock clock = new();
    private readonly RecordedSound sound = new();
    private int posts;

    public void Dispose() => sound.Dispose();

    /// <summary>An app whose window thread is the test's, so what decoding posts back runs only when the test lets it.</summary>
    private TestApp App() => new(Returning, post: action =>
    {
        Interlocked.Increment(ref posts);
        Dispatcher.UIThread.Post(action);
    });

    private AppParts Parts(TestApp app, bool withSound = true) =>
        app.Parts with { Clock = clock, OpenSound = withSound ? _ => sound : null };

    /// <summary>Keeps <paramref name="bytes"/> in the app's cache as <paramref name="file"/>, and plays it from there.</summary>
    private static async Task<CardClip> KeptAsync(TestApp app, byte[] bytes, CameraFile? file = null)
    {
        file ??= Video;
        using (var pending = app.Parts.Clips.Start(file))
        {
            pending.Output.Write(bytes);
            pending.Keep();
        }

        var clip = app.Controller.Play(file, app.Parts.Clips)!;
        (await clip.Arrival).ShouldBeOfType<ClipArrival.Kept>();
        return clip;
    }

    /// <summary>Lets the window's thread run until <paramref name="condition"/> holds.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The player never got there.");
            }

            await Task.Delay(10);
        }
    }

    [AvaloniaFact]
    public async Task A_kept_clip_plays_its_pictures_and_sound_from_the_start_to_the_end()
    {
        await using var app = App();
        var clip = await KeptAsync(app, TestPictures.Clip());
        using var player = new PlayerViewModel(clip, Parts(app), () => { });

        player.Tick();
        await UntilAsync(() => player.Picture is not null);

        player.Title.ShouldBe("MOVI0007");
        player.Format.ShouldBe("1920×1080 · 4 fps");
        player.Time.ShouldBe("0:00 / 0:01");
        player.Length.ShouldBe(1);
        player.Status.ShouldBe("");
        player.Playing.ShouldBeTrue();
        player.PlayLabel.ShouldBe("Pause");
        sound.Played.Count.ShouldBe(2);

        clock.Advance(TimeSpan.FromSeconds(0.6));
        player.Tick();
        player.Position.ShouldBe(0.6, tolerance: 0.001);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        player.Tick();

        player.Playing.ShouldBeFalse();
        player.PlayLabel.ShouldBe("Play");
        player.Position.ShouldBe(1);
        sound.Stops.ShouldBe(1);

        // Played again from the start.
        player.PlayPauseCommand.Execute(null);
        player.Playing.ShouldBeTrue();
        player.Position.ShouldBe(0);
    }

    [AvaloniaFact]
    public async Task Pausing_holds_the_picture_and_silences_it_and_a_drag_goes_where_it_is_dropped()
    {
        await using var app = App();
        var clip = await KeptAsync(app, TestPictures.Clip());
        using var player = new PlayerViewModel(clip, Parts(app), () => { });
        player.Tick();
        await UntilAsync(() => player.Picture is not null);
        var first = player.Picture;

        player.PlayPauseCommand.Execute(null);
        clock.Advance(TimeSpan.FromSeconds(0.5));
        player.Tick();

        player.Playing.ShouldBeFalse();
        player.Position.ShouldBe(0);
        sound.Stops.ShouldBe(1);

        player.Position = 0.75;
        player.Tick();
        await UntilAsync(() => !ReferenceEquals(player.Picture, first));

        player.Position.ShouldBe(0.75);
        player.Time.ShouldBe("0:00 / 0:01");
        player.Playing.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Pictures_falling_due_while_one_decodes_wait_and_only_the_newest_is_shown()
    {
        await using var app = App();
        var clip = await KeptAsync(app, TestPictures.Clip());
        using var player = new PlayerViewModel(clip, Parts(app), () => { });

        // The first starts decoding; the second and third fall due before it is back, and only the third waits.
        player.Tick();
        clock.Advance(TimeSpan.FromSeconds(0.25));
        player.Tick();
        clock.Advance(TimeSpan.FromSeconds(0.25));
        player.Tick();
        await UntilAsync(() => Volatile.Read(ref posts) >= 2 && player.Picture is not null);

        posts.ShouldBe(2);
    }

    [AvaloniaFact]
    public async Task A_clip_without_sound_or_a_sound_device_plays_its_pictures_silent()
    {
        await using var app = App();
        var silent = await KeptAsync(app, TestPictures.Clip(withSound: false));
        using var player = new PlayerViewModel(silent, Parts(app), () => { });
        player.Tick();
        await UntilAsync(() => player.Picture is not null);
        sound.Played.ShouldBeEmpty();

        var clip = await KeptAsync(app, TestPictures.Clip(), Video with { Index = 8 });
        using var deaf = new PlayerViewModel(clip, Parts(app, withSound: false), () => { });
        deaf.Tick();
        deaf.Playing.ShouldBeTrue();

        // A PC whose sound device will not open plays it silent too.
        var another = await KeptAsync(app, TestPictures.Clip(), Video with { Index = 9 });
        using var unheard = new PlayerViewModel(another, app.Parts with { Clock = clock, OpenSound = _ => null }, () => { });
        unheard.Tick();
        unheard.Playing.ShouldBeTrue();
        sound.Played.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task A_picture_that_does_not_decode_leaves_the_last_one_showing()
    {
        await using var app = App();
        var clip = await KeptAsync(app, TestPictures.Clip(decodable: false));
        using var player = new PlayerViewModel(clip, Parts(app), () => { });

        player.Tick();
        await UntilAsync(() => Volatile.Read(ref posts) >= 1);

        player.Picture.ShouldBeNull();
        player.Playing.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_clip_still_coming_off_the_card_says_when_it_will_play()
    {
        await using var app = App();
        // Eighty pictures at eight a second, an eighth of a second of sound after each: ten seconds of clip.
        var slow = (FakeClip.Reference(
            [.. Enumerable.Range(0, 80).Select(_ => TestPictures.Jpeg(90))],
            [.. Enumerable.Range(0, 80).Select(_ => new byte[4_000])])
            with { MicrosPerFrame = 125_000, Rate = 8 }).Build();
        app.Control.AddFile('A', Sunday, slow);
        await app.ConnectedAsync();
        await app.Controller.RefreshLibrary()!;
        app.Control.DownloadChunk = 500;
        app.Control.AnswerDelay = TimeSpan.FromMilliseconds(30);
        var clip = app.Controller.Play(app.Controller.State.Library.Files!.Single(), app.Parts.Clips)!;
        using var player = new PlayerViewModel(clip, Parts(app), () => { });
        player.Tick();
        player.Status.ShouldBe("Getting the clip ready.");

        await UntilAsync(() => clip.Reader.Ready > TimeSpan.Zero);
        var first = clip.Reader.Ready;
        player.Tick();
        clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => clip.Reader.Ready >= first + TimeSpan.FromSeconds(0.25));
        player.Tick();

        player.Status.ShouldStartWith("Fetching the clip from the card. It plays in ");
        player.Playing.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_file_that_is_no_clip_says_why_it_will_not_play()
    {
        await using var app = App();
        app.Control.AddFile('A', Sunday, TestPictures.Jpeg(30));
        await app.ConnectedAsync();
        await app.Controller.RefreshLibrary()!;
        var clip = app.Controller.Play(app.Controller.State.Library.Files!.Single(), app.Parts.Clips)!;
        await clip.Arrival;
        using var player = new PlayerViewModel(clip, Parts(app), () => { });

        player.Tick();

        player.Status.ShouldBe("This file cannot be played. This is not an AVI file.");
        player.Format.ShouldBe("");
    }

    [AvaloniaFact]
    public async Task A_clip_this_PC_cannot_read_stops_and_says_so()
    {
        await using var app = App();
        var clip = await KeptAsync(app, TestPictures.Clip());
        using var player = new PlayerViewModel(clip, Parts(app), () => { });

        using (new FileStream(app.Parts.Clips.PathFor(Video), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            player.Tick();
        }

        player.Status.ShouldStartWith("This PC could not read the clip: ");
    }

    [AvaloniaFact]
    public async Task Closing_lets_go_of_the_clip_and_its_sound_and_a_picture_decoded_too_late_is_dropped()
    {
        await using var app = App();
        var clip = await KeptAsync(app, TestPictures.Clip());
        var closed = false;
        var player = new PlayerViewModel(clip, Parts(app), () => closed = true);
        player.Tick();

        player.CloseCommand.Execute(null);
        player.Dispose();
        player.Dispose();
        await UntilAsync(() => Volatile.Read(ref posts) >= 1);

        closed.ShouldBeTrue();
        sound.Disposed.ShouldBeTrue();
        player.Picture.ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => new PlayerViewModel(null!, app.Parts, () => { }));
        Should.Throw<ArgumentNullException>(() => new PlayerViewModel(clip, null!, () => { }));
        Should.Throw<ArgumentNullException>(() => new PlayerViewModel(clip, app.Parts, null!));
    }

    /// <summary>A clock moved by the test.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>A sound device that keeps what it was given and plays none of it.</summary>
    private sealed class RecordedSound : ISoundDevice
    {
        public List<byte[]> Played { get; } = [];

        public int Stops { get; private set; }

        public bool Disposed { get; private set; }

        public long Holding => Played.Sum(p => (long)p.Length);

        public void Play(byte[] pcm) => Played.Add(pcm);

        public void Stop()
        {
            Stops++;
            Played.Clear();
        }

        public void Dispose() => Disposed = true;
    }
}
