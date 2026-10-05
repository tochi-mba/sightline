using Shouldly;
using Sightline.App.Services;
using Xunit;

namespace Sightline.App.Tests.Services;

/// <summary>What an alarm does on this PC.</summary>
public sealed class WindowsSentryActionsTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 21, 40, 7, TimeSpan.Zero);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "sightline-sentry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        if (File.Exists(folder))
        {
            File.Delete(folder);
        }
    }

    [Fact]
    public void An_alarm_saves_its_picture_named_for_the_local_time_and_says_so()
    {
        var actions = new WindowsSentryActions(folder, TimeZoneInfo.CreateCustomTimeZone("BST", TimeSpan.FromHours(1), "BST", "BST"));
        var seen = new List<AlarmNotice>();
        actions.Raised += seen.Add;

        actions.AlarmRaised(At, [1, 2, 3], saveSnapshot: true);
        actions.AlarmRaised(At, [4], saveSnapshot: true);
        actions.AlarmEnded();

        seen[0].SavedTo.ShouldBe(Path.Combine(folder, "sentry-20261005-224007.jpg"));
        seen[1].SavedTo.ShouldBe(Path.Combine(folder, "sentry-20261005-224007-2.jpg"));
        File.ReadAllBytes(seen[0].SavedTo!).ShouldBe([1, 2, 3]);
        seen[0].At.ShouldBe(At);
    }

    [Fact]
    public void Without_saving_or_with_a_disk_that_refuses_an_alarm_still_tells()
    {
        var actions = new WindowsSentryActions(folder);
        var seen = new List<AlarmNotice>();
        actions.Raised += seen.Add;

        actions.AlarmRaised(At, [1], saveSnapshot: false);
        Directory.Exists(folder).ShouldBeFalse();

        File.WriteAllText(folder, "a file where the folder should be");
        actions.AlarmRaised(At, [1], saveSnapshot: true);

        seen.Select(n => n.SavedTo).ShouldBe([null, null]);
    }

    [Fact]
    public void Nobody_listening_is_fine_and_its_parts_are_required()
    {
        new WindowsSentryActions(folder).AlarmRaised(At, [1], saveSnapshot: false);
        Should.Throw<ArgumentException>(() => new WindowsSentryActions(" "));
        Should.Throw<ArgumentNullException>(() => new WindowsSentryActions(folder).AlarmRaised(At, null!, false));
        WindowsSentryActions.DefaultFolder.ShouldEndWith(Path.Combine("Sightline", "Sentry"));
    }
}
