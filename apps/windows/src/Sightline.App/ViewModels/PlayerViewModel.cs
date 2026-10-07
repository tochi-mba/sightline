using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sightline.Core.Camera;
using Sightline.Core.Playback;

namespace Sightline.App.ViewModels;

/// <summary>
/// A video from the card, playing while it is still being fetched: its pictures and sound, where it is, and why it
/// waits when it does.
/// </summary>
/// <remarks>
/// A timer on the window's thread steps the player about sixty times a second. A picture that falls due is read from
/// the clip's file there, a small read, and decoded on another thread; while one decodes only the newest waits, so a
/// slow decode drops pictures rather than holding the clip back. The sound is read on the window's thread too, which
/// is the only one that touches the file.
/// </remarks>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(15);

    private readonly CardClip clip;
    private readonly AppParts parts;
    private readonly Action close;
    private readonly ClipPlayer player;
    private readonly DispatcherTimer timer;
    private Stream? file;
    private ISoundDevice? sound;
    private SoundFeed? feed;
    private bool soundOpened;
    private bool decoding;
    private byte[]? newest;
    private bool following;
    private bool closed;
    private string? unreadable;

    /// <summary>A player for <paramref name="clip"/>, which it lets go of when disposed.</summary>
    /// <param name="clip">The clip, arriving or kept.</param>
    /// <param name="parts">The window's parts: its decoder, its thread, its clock and its sound.</param>
    /// <param name="close">What closing the player does.</param>
    public PlayerViewModel(CardClip clip, AppParts parts, Action close)
    {
        this.clip = clip ?? throw new ArgumentNullException(nameof(clip));
        this.parts = parts ?? throw new ArgumentNullException(nameof(parts));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
        var clock = parts.Clock ?? TimeProvider.System;
        var start = clock.GetTimestamp();
        player = new ClipPlayer(clip.Reader, () => clock.GetElapsedTime(start));
        Title = clip.File.DisplayName;
        timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Tick());
        timer.Start();
    }

    /// <summary>The clip's name, in the camera's style.</summary>
    public string Title { get; }

    /// <summary>The picture showing now.</summary>
    [ObservableProperty]
    private Bitmap? picture;

    /// <summary>What the clip really is, from its own header: its size in pixels and its pictures a second.</summary>
    [ObservableProperty]
    private string format = "";

    /// <summary>Where it is, of how long it plays.</summary>
    [ObservableProperty]
    private string time = "0:00 / 0:00";

    /// <summary>Why nothing moves, when nothing does; empty while it plays.</summary>
    [ObservableProperty]
    private string status = "Getting the clip ready.";

    /// <summary>Whether it plays, or will as soon as it can; what the button offers is the other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private bool playing = true;

    /// <summary>How long it plays, in seconds, for the bar.</summary>
    [ObservableProperty]
    private double length;

    /// <summary>Where it is, in seconds; setting it goes there, as far as the clip has arrived.</summary>
    [ObservableProperty]
    private double position;

    /// <summary>The play button's label.</summary>
    public string PlayLabel => Playing ? "Pause" : "Play";

    /// <summary>Moves the clip on to now: shows the picture due, plays the sound due, and says where it is.</summary>
    public void Tick()
    {
        var step = player.Step();
        try
        {
            if (step.Picture is { } due)
            {
                Show(Read(due.Offset, due.Length));
            }

            Sound(step);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Nothing more of it can be read, so nothing more can be shown.
            unreadable = $"This PC could not read the clip: {failure.Message}";
            timer.Stop();
        }

        Describe();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        timer.Stop();
        clip.Dispose();
        sound?.Dispose();
        file?.Dispose();
        Picture?.Dispose();
    }

    partial void OnPositionChanged(double value)
    {
        if (!following)
        {
            player.Seek(TimeSpan.FromSeconds(value));
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (player.Phase is PlayerPhase.Paused or PlayerPhase.Ended)
        {
            player.Play();
        }
        else
        {
            player.Pause();
        }

        Tick();
    }

    [RelayCommand]
    private void Close() => close();

    private byte[] Read(long offset, int count)
    {
        file ??= clip.OpenRead();
        var bytes = new byte[count];
        file.Position = offset;
        file.ReadExactly(bytes);
        return bytes;
    }

    private void Sound(PlayerStep step)
    {
        if (!soundOpened && clip.Reader.Clip is { } avi)
        {
            soundOpened = true;
            if (avi.Sound is { } format && parts.OpenSound?.Invoke(format) is { } device)
            {
                sound = device;
                feed = new SoundFeed(device, format, Read);
            }
        }

        feed?.Follow(player.Phase, step);
    }

    private void Show(byte[] jpeg)
    {
        if (decoding)
        {
            newest = jpeg;
            return;
        }

        Decode(jpeg);
    }

    private void Decode(byte[] jpeg)
    {
        decoding = true;
        _ = Task.Run(() =>
        {
            var decoded = parts.Decode(jpeg);
            parts.Post(() => Decoded(decoded));
        });
    }

    private void Decoded(Bitmap? decoded)
    {
        if (closed)
        {
            decoded?.Dispose();
            return;
        }

        if (decoded is not null)
        {
            var previous = Picture;
            Picture = decoded;
            previous?.Dispose();
        }

        if (newest is { } next)
        {
            newest = null;
            Decode(next);
        }
        else
        {
            decoding = false;
        }
    }

    private void Describe()
    {
        if (clip.Reader.Clip is { } avi)
        {
            Format = string.Create(CultureInfo.InvariantCulture, $"{avi.Width}×{avi.Height} · {avi.FramesPerSecond:0.##} fps");
        }

        Length = player.Duration.TotalSeconds;
        following = true;
        Position = player.Position.TotalSeconds;
        following = false;
        Time = $"{CameraWords.Clock(player.Position)} / {CameraWords.Clock(player.Duration)}";
        Playing = player.Phase is PlayerPhase.Playing or PlayerPhase.Waiting;
        Status = unreadable
            ?? (clip.Arrival is { IsCompletedSuccessfully: true, Result: ClipArrival.Failed failed } ? failed.Reason : null)
            ?? (player.Phase != PlayerPhase.Waiting ? ""
                : player.StartsIn is { } wait ? $"Fetching the clip from the card. It plays in {CameraWords.Clock(wait)}."
                : "Getting the clip ready.");
    }
}
