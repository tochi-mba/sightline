namespace Sightline.Benchmarks.Camera;

/// <summary>
/// Finished reads, made once for each length and handed out again after that.
/// </summary>
/// <remarks>
/// A transport's read returns a <see cref="Task{TResult}"/>, and a new one per read would put the
/// fake's own allocation into every figure. A real socket's read costs something too, but that
/// cost belongs to the operating system and stays the same whatever Sightline changes, so the fakes
/// leave it out and every byte a benchmark reports is the protocol's own.
/// </remarks>
internal static class CompletedReads
{
    /// <summary>Room for the largest buffer anything here reads into: the control channel's 64 KB.</summary>
    private static readonly Task<int>?[] Made = new Task<int>?[(64 * 1024) + 1];

    /// <summary>A read that has already finished with <paramref name="count"/> bytes.</summary>
    public static Task<int> Of(int count) => Made[count] ??= Task.FromResult(count);
}
