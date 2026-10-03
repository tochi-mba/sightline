namespace Sightline.Benchmarks;

/// <summary>
/// Checks that a benchmark's setup produced what the benchmark claims to measure.
/// </summary>
/// <remarks>
/// A benchmark that quietly measured the wrong thing would still print a fast and convincing
/// number: a stream that never completes a picture is very cheap to reassemble. So each setup runs
/// its benchmarks once and checks the answers before anything is timed, and a broken one stops the
/// run instead of entering the baseline.
/// </remarks>
internal static class Expect
{
    /// <summary>Stops the run unless <paramref name="actual"/> is <paramref name="expected"/>.</summary>
    /// <param name="expected">What the benchmark is meant to produce.</param>
    /// <param name="actual">What it produced.</param>
    /// <param name="what">The quantity, in words, for the message.</param>
    /// <exception cref="InvalidOperationException">They differ.</exception>
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {what} to be {expected}, but it was {actual}.");
        }
    }
}
