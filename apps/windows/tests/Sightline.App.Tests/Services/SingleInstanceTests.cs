using Shouldly;
using Sightline.App.Services;
using Xunit;

namespace Sightline.App.Tests.Services;

/// <summary>One copy of Sightline at a time.</summary>
public sealed class SingleInstanceTests
{
    private readonly string name = "SightlineTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_second_start_wakes_the_first_and_does_not_carry_on()
    {
        using var first = new SingleInstance(name);
        var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.OnWake(() => woken.TrySetResult());
        first.IsFirst.ShouldBeTrue();

        using (var second = new SingleInstance(name))
        {
            second.IsFirst.ShouldBeFalse();
            second.WakeFirst();
        }

        await woken.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Listening_again_replaces_the_last_listener()
    {
        using var first = new SingleInstance(name);
        var stale = 0;
        var current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.OnWake(() => Interlocked.Increment(ref stale));
        first.OnWake(() => current.TrySetResult());

        using var second = new SingleInstance(name);
        second.WakeFirst();

        await current.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref stale).ShouldBe(0);
    }

    [Fact]
    public void Once_the_first_has_gone_the_next_start_is_first()
    {
        new SingleInstance(name).Dispose();

        using var next = new SingleInstance(name);

        next.IsFirst.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => new SingleInstance(" "));
        Should.Throw<ArgumentNullException>(() => next.OnWake(null!));
    }
}
