using Shouldly;
using Sightline.Core.Playback;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Core.Tests.Playback;

/// <summary>A clip from the card as a player holds it: its bytes, and how its arrival ended.</summary>
public sealed class CardClipTests
{
    private static readonly CameraFile Video = new('A', 1, null, 1);

    [Fact]
    public void Before_its_bytes_arrive_there_is_nothing_to_open()
    {
        using var clip = new CardClip(Video);

        clip.File.ShouldBe(Video);
        clip.HasBytes.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(clip.OpenRead).Message.ShouldBe("The clip has not started arriving.");
        clip.Arrival.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task How_its_arrival_ended_is_told_once_and_the_first_word_stands()
    {
        using var clip = new CardClip(Video);

        clip.Kept("kept.avi");
        clip.Failed("too late");
        clip.Stopped();

        (await clip.Arrival).ShouldBe(new ClipArrival.Kept("kept.avi"));
        clip.HasBytes.ShouldBeTrue();
    }

    [Fact]
    public void Letting_it_go_twice_is_the_same_as_once()
    {
        var clip = new CardClip(Video);

        clip.Dispose();
        clip.Dispose();

        clip.Stopping.IsCancellationRequested.ShouldBeTrue();
    }
}
