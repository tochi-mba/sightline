using Shouldly;
using Sightline.Protocol.Rtp;
using Xunit;

namespace Sightline.Core.Tests;

/// <summary>The one feed a session shares, on its own: what it does at the edges a session reaches only by racing.</summary>
public sealed class LiveFeedTests
{
    [Fact]
    public async Task A_feed_that_is_over_takes_no_new_watcher_and_tells_the_ones_it_had_why()
    {
        var retired = new TaskCompletionSource<LiveFeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new LiveFeed(
            _ => throw new RtspException("The camera refused the video track (454)."),
            TimeSpan.FromSeconds(1),
            () => null,
            ended => retired.TrySetResult(ended),
            out var first);

        (await retired.Task).ShouldBeSameAs(feed);
        feed.TrySubscribe().ShouldBeNull();
        (await Should.ThrowAsync<RtspException>(() => first.NextAsync(CancellationToken.None)))
            .Message.ShouldBe("The camera refused the video track (454).");

        first.Dispose();
        await feed.DisposeAsync();
        await feed.DisposeAsync();
    }
}
