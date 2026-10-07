using Shouldly;
using Sightline.Core.Playback;
using Sightline.Protocol.Media;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>A clip's sound, fed to the device a second ahead, and dropped the moment the player stops or jumps.</summary>
public sealed class SoundFeedTests : IDisposable
{
    // 16 kHz mono 16-bit, as the reference camera records: 32,000 bytes a second.
    private static readonly AviSound Format = new(16_000, 1, 16);

    private readonly FakeDevice device = new();
    private readonly List<(long Offset, int Length)> reads = [];

    public void Dispose() => device.Dispose();

    private SoundFeed Feed() => new(device, Format, (offset, length) =>
    {
        reads.Add((offset, length));
        return new byte[length];
    });

    private static PlayerStep Step(bool restarts, params (long Offset, int Length)[] slices) =>
        new(null, [.. slices.Select(s => new SoundSlice(TimeSpan.Zero, s.Offset, s.Length))], restarts);

    [Fact]
    public void Sound_is_read_from_the_clip_and_played_in_order_a_second_ahead_and_no_further()
    {
        var feed = Feed();

        feed.Follow(PlayerPhase.Playing, Step(true, (100, 16_000), (20_000, 16_000), (40_000, 16_000)));

        reads.ShouldBe([(100, 16_000), (20_000, 16_000)]);
        device.Played.Count.ShouldBe(2);
        device.Stops.ShouldBe(0);

        device.Finish(1);
        feed.Follow(PlayerPhase.Playing, Step(false, (60_000, 16_000)));

        reads.ShouldBe([(100, 16_000), (20_000, 16_000), (40_000, 16_000)]);
        device.Finish(2);
        feed.Follow(PlayerPhase.Playing, Step(false));
        reads[^1].ShouldBe((60_000, 16_000));
    }

    [Fact]
    public void A_jump_drops_what_was_held_and_what_waited_and_plays_from_the_new_place()
    {
        var feed = Feed();
        feed.Follow(PlayerPhase.Playing, Step(true, (100, 16_000), (20_000, 16_000), (40_000, 16_000)));

        feed.Follow(PlayerPhase.Playing, Step(true, (90_000, 8_000)));

        device.Stops.ShouldBe(1);
        reads[^1].ShouldBe((90_000, 8_000));
        device.Holding.ShouldBe(8_000);
    }

    [Fact]
    public void Sound_stops_the_moment_the_player_does_and_not_again_until_it_plays()
    {
        var feed = Feed();
        feed.Follow(PlayerPhase.Waiting, Step(false));
        device.Stops.ShouldBe(0);
        feed.Follow(PlayerPhase.Playing, Step(true, (100, 16_000), (20_000, 16_000), (40_000, 16_000)));

        feed.Follow(PlayerPhase.Paused, Step(false));
        feed.Follow(PlayerPhase.Paused, Step(false));

        device.Stops.ShouldBe(1);
        device.Holding.ShouldBe(0);
        reads.Count.ShouldBe(2);
    }

    [Fact]
    public void A_feed_needs_a_device_a_format_and_the_clip()
    {
        Should.Throw<ArgumentNullException>(() => new SoundFeed(null!, Format, (_, _) => []));
        Should.Throw<ArgumentNullException>(() => new SoundFeed(device, null!, (_, _) => []));
        Should.Throw<ArgumentNullException>(() => new SoundFeed(device, Format, null!));
    }

    private sealed class FakeDevice : ISoundDevice
    {
        public List<byte[]> Played { get; } = [];

        public int Stops { get; private set; }

        public long Holding => Played.Sum(p => (long)p.Length);

        public void Play(byte[] pcm) => Played.Add(pcm);

        public void Stop()
        {
            Stops++;
            Played.Clear();
        }

        /// <summary>The first <paramref name="count"/> pieces finish playing.</summary>
        public void Finish(int count) => Played.RemoveRange(0, count);

        public void Dispose()
        {
        }
    }
}
