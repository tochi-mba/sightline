namespace Sightline.Core.Tests.Sentry;

/// <summary>A clock moved by the test, from whatever thread moves it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 21, 40, 0, TimeSpan.Zero);
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}
