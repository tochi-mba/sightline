using Shouldly;
using Sightline.Core.Playback;
using Sightline.Testing;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>Playing a clip still coming off the card, on a clock the test turns.</summary>
public sealed class ClipPlayerTests
{
    // Two pictures and half a second of sound in each run, as the camera interleaves them.
    private static readonly byte[] Picture = new byte[100];
    private static readonly byte[] HalfSecond = new byte[16_000];

    private TimeSpan clock;

    [Fact]
    public void A_clip_that_has_all_arrived_plays_at_once_each_picture_at_its_time_and_ends()
    {
        var reader = Arrived(runs: 2);
        var player = Player(reader);

        var first = player.Step();
        player.Phase.ShouldBe(PlayerPhase.Playing);
        first.Picture!.Value.At.ShouldBe(TimeSpan.Zero);
        first.SoundRestarts.ShouldBeTrue();
        first.Sound.Count.ShouldBe(2);

        Turn(0.1);
        player.Step().Picture.ShouldBeNull();
        Turn(0.2);
        player.Step().Picture!.Value.At.ShouldBe(TimeSpan.FromSeconds(0.25));
        Turn(0.8);
        player.Step();

        player.Phase.ShouldBe(PlayerPhase.Ended);
        player.Position.ShouldBe(TimeSpan.FromSeconds(1));
        player.Duration.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Played_again_after_the_end_it_starts_from_the_beginning()
    {
        var player = Player(Arrived(runs: 1));
        player.Step();
        Turn(1);
        player.Step();
        player.Phase.ShouldBe(PlayerPhase.Ended);

        player.Play();
        var step = player.Step();

        player.Phase.ShouldBe(PlayerPhase.Playing);
        player.Position.ShouldBe(TimeSpan.Zero);
        step.Picture!.Value.At.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_clip_arriving_slower_than_it_plays_waits_until_the_rest_will_arrive_in_time()
    {
        // Two-thirds as fast as it plays, as a 1080p clip comes off the card.
        var file = Clip(runs: 12);
        var reader = new ClipReader();
        var player = Player(reader);
        var sent = Feed(reader, file, upTo: 0);

        for (var tick = 1; tick <= 120; tick++)
        {
            clock = TimeSpan.FromSeconds(tick * 0.1);
            sent = Feed(reader, file, upTo: (int)(file.Length * Math.Min(1, tick * 0.1 / 9)));
            player.Step();
            if (player.Phase == PlayerPhase.Playing)
            {
                break;
            }
        }

        // Six seconds of clip arriving at two-thirds speed: about two seconds must be ready first.
        player.Phase.ShouldBe(PlayerPhase.Playing);
        reader.Ready.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));
        reader.IsComplete.ShouldBeFalse();
        sent.ShouldBeLessThan(file.Length);
    }

    [Fact]
    public void While_it_waits_it_says_how_long_until_it_can_play()
    {
        var file = Clip(runs: 12);
        var reader = new ClipReader();
        var player = Player(reader);
        Feed(reader, file, upTo: file.Length / 8);
        player.Step();
        player.StartsIn.ShouldBeNull();

        Turn(1.5);
        Feed(reader, file, upTo: file.Length / 4);
        player.Step();

        player.Phase.ShouldBe(PlayerPhase.Waiting);
        player.StartsIn!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void A_clip_that_stops_arriving_mid_play_waits_rather_than_skips()
    {
        var file = Clip(runs: 4);
        var reader = new ClipReader();
        var player = Player(reader, lead: TimeSpan.FromSeconds(0.5));
        Feed(reader, file, upTo: file.Length / 2);
        player.Step();
        Turn(2);
        Feed(reader, file, upTo: file.Length * 3 / 4);
        player.Step();
        player.Phase.ShouldBe(PlayerPhase.Playing);

        Turn(5);
        player.Step();

        player.Phase.ShouldBe(PlayerPhase.Waiting);
        player.Position.ShouldBe(reader.Ready);
    }

    [Fact]
    public void Pausing_holds_the_place_and_playing_again_carries_on_from_it()
    {
        var player = Player(Arrived(runs: 4));
        player.Step();
        Turn(0.5);
        player.Pause();
        player.Phase.ShouldBe(PlayerPhase.Paused);
        player.Position.ShouldBe(TimeSpan.FromSeconds(0.5));

        Turn(3);
        player.Step();
        player.Position.ShouldBe(TimeSpan.FromSeconds(0.5));

        player.Play();
        var resumed = player.Step();
        player.Phase.ShouldBe(PlayerPhase.Playing);
        resumed.SoundRestarts.ShouldBeTrue();
        resumed.Sound[0].At.ShouldBe(TimeSpan.FromSeconds(0.5));
        Turn(0.25);
        player.Step();
        player.Position.ShouldBe(TimeSpan.FromSeconds(0.75));
    }

    [Fact]
    public void Play_pressed_while_it_plays_changes_nothing()
    {
        var player = Player(Arrived(runs: 2));
        player.Step();
        Turn(0.5);

        player.Play();
        player.Step();

        player.Phase.ShouldBe(PlayerPhase.Playing);
        player.Position.ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void Before_its_headers_arrive_there_is_nothing_to_play_or_show()
    {
        var player = Player(new ClipReader());

        var step = player.Step();

        player.Phase.ShouldBe(PlayerPhase.Waiting);
        step.Picture.ShouldBeNull();
        player.StartsIn.ShouldBeNull();
    }

    [Fact]
    public void A_clip_that_has_stopped_arriving_cannot_say_when_it_will_play()
    {
        var file = Clip(runs: 12);
        var reader = new ClipReader();
        var player = Player(reader);
        Feed(reader, file, upTo: file.Length / 8);
        player.Step();

        Turn(2);
        player.Step();

        player.Phase.ShouldBe(PlayerPhase.Waiting);
        player.StartsIn.ShouldBeNull();
    }

    [Fact]
    public void Paused_before_it_starts_it_does_not_start()
    {
        var player = Player(Arrived(runs: 2));

        player.Pause();
        player.Step();

        player.Phase.ShouldBe(PlayerPhase.Paused);
    }

    [Fact]
    public void A_jump_goes_only_as_far_as_has_arrived_and_starts_the_sound_again_there()
    {
        var player = Player(Arrived(runs: 4));
        player.Step();

        player.Seek(TimeSpan.FromSeconds(1.25));
        var step = player.Step();
        step.SoundRestarts.ShouldBeTrue();
        step.Sound[0].At.ShouldBe(TimeSpan.FromSeconds(1.25));
        step.Picture!.Value.At.ShouldBe(TimeSpan.FromSeconds(1.25));

        player.Seek(TimeSpan.FromMinutes(5));
        player.Position.ShouldBe(TimeSpan.FromSeconds(2));
        player.Seek(-TimeSpan.FromSeconds(1));
        player.Position.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_jump_back_after_the_end_plays_on_and_a_paused_one_stays_paused()
    {
        var player = Player(Arrived(runs: 1));
        player.Step();
        Turn(1);
        player.Step();

        player.Seek(TimeSpan.FromSeconds(0.25));
        player.Phase.ShouldBe(PlayerPhase.Waiting);
        player.Step().Picture.ShouldNotBeNull();
        player.Phase.ShouldBe(PlayerPhase.Playing);

        Turn(1);
        player.Step();
        player.Pause();
        player.Seek(TimeSpan.Zero);
        player.Phase.ShouldBe(PlayerPhase.Paused);
    }

    [Fact]
    public void Sound_that_arrives_while_playing_is_queued_behind_what_is_queued()
    {
        // Arriving as fast as it plays, so playing starts with a second of it ready.
        var file = Clip(runs: 6);
        var reader = new ClipReader();
        var player = Player(reader, lead: TimeSpan.FromSeconds(0.5));
        Feed(reader, file, upTo: file.Length / 3);
        player.Step();
        Turn(1);
        Feed(reader, file, upTo: file.Length * 2 / 3);
        var first = player.Step();
        player.Phase.ShouldBe(PlayerPhase.Playing);
        first.SoundRestarts.ShouldBeTrue();
        var queued = first.Sound.Count;

        Feed(reader, file, upTo: file.Length);
        Turn(0.1);
        var next = player.Step();

        next.SoundRestarts.ShouldBeFalse();
        next.Sound.Count.ShouldBe(6 - queued);
        Turn(0.1);
        player.Step().Sound.ShouldBeEmpty();
    }

    [Fact]
    public void A_player_needs_a_clip_and_a_clock()
    {
        Should.Throw<ArgumentNullException>(() => new ClipPlayer(null!, () => TimeSpan.Zero));
        Should.Throw<ArgumentNullException>(() => new ClipPlayer(new ClipReader(), null!));
        new ClipPlayer(new ClipReader(), () => TimeSpan.Zero).Duration.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>
    /// A clip of <paramref name="runs"/> half-seconds, two pictures and a run of sound in each, whose header says as
    /// much: four pictures a second, so its length by the header is its length by its sound.
    /// </summary>
    private static byte[] Clip(int runs) =>
        (FakeClip.Reference([.. Enumerable.Repeat(Picture, runs * 2)], [.. Enumerable.Repeat(HalfSecond, runs)]) with
        {
            PicturesPerSound = 2,
            MicrosPerFrame = 250_000,
            Rate = 4,
        }).Build();

    private static ClipReader Arrived(int runs)
    {
        var reader = new ClipReader();
        reader.Push(Clip(runs));
        reader.Finish();
        return reader;
    }

    /// <summary>Sends the file on up to <paramref name="upTo"/> bytes, finishing it once it is all sent.</summary>
    private static int Feed(ClipReader reader, byte[] file, int upTo)
    {
        var from = (int)reader.BytesRead;
        if (upTo > from)
        {
            reader.Push(file.AsSpan(from, upTo - from));
            if (upTo == file.Length)
            {
                reader.Finish();
            }
        }

        return Math.Max(from, upTo);
    }

    private ClipPlayer Player(ClipReader reader, TimeSpan? lead = null) => new(reader, () => clock, lead);

    private void Turn(double seconds) => clock += TimeSpan.FromSeconds(seconds);
}
