namespace Sightline.Core.Updates;

/// <summary>What one version brought, in words a person reads once.</summary>
/// <param name="Version">The version that introduced it, as bare numbers: <c>0.2.0</c>.</param>
/// <param name="Lines">A few short lines, each one thing somebody can now do.</param>
public sealed record WhatsNewEntry(string Version, IReadOnlyList<string> Lines);

/// <summary>
/// Decides whether an update is worth a word, and which words.
/// </summary>
/// <remarks>
/// <para>
/// After the app updates itself, the next launch may greet the person with what changed — but only
/// when something changed <em>for them</em>. The notes here are curated by hand, a few lines per
/// version that earned them; most updates are fixes and ship silently. A first install says
/// nothing either: everything is new, and the app itself is the introduction.
/// </para>
/// <para>
/// Versions compare by their numbers only. Rolling builds of one version
/// (<c>0.2.0-rolling.12</c> and <c>.13</c>) are the same version to a person, so moving between
/// them never greets; and a downgrade claims nothing, because nothing is new.
/// </para>
/// </remarks>
public static class WhatsNewCatalog
{
    /// <summary>
    /// The curated notes, newest first. A version with nothing worth a person's time has no entry.
    /// </summary>
    public static IReadOnlyList<WhatsNewEntry> Entries { get; } =
    [
        // 0.1.0 is the first version; a first install greets nobody, so it needs no entry yet.
        // When 0.2.0 ships, its entry goes here, above this comment.
    ];

    /// <summary>
    /// The notes to show when the app that last ran was <paramref name="previous"/> and this one is
    /// <paramref name="current"/>; empty when nothing needs saying.
    /// </summary>
    /// <param name="previous">The version recorded at the last run, or null on a first run.</param>
    /// <param name="current">This build's version.</param>
    /// <param name="entries">The notes to choose from; the curated ones when omitted.</param>
    public static IReadOnlyList<WhatsNewEntry> Since(
        string? previous, string current, IReadOnlyList<WhatsNewEntry>? entries = null)
    {
        var from = Numbers(previous);
        var to = Numbers(current);
        if (from is null || to is null || from >= to)
        {
            // A first run, an unreadable record, the same version, or a downgrade: say nothing.
            // An unreadable record reads as a first run because greeting somebody with notes for
            // every version ever shipped would be worse than greeting them with none.
            return [];
        }

        return (entries ?? Entries)
            .Where(entry => Numbers(entry.Version) is { } at && at > from && at <= to)
            .OrderByDescending(entry => Numbers(entry.Version))
            .ToList();
    }

    /// <summary>
    /// A version's numbers, with anything after them ignored, or null when there are none.
    /// </summary>
    /// <remarks><c>0.2.0-rolling.12+g1abc</c> reads as 0.2.0: the suffix orders builds, not features.</remarks>
    public static Version? Numbers(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var bare = version.AsSpan();
        var suffix = bare.IndexOfAny('-', '+');
        if (suffix >= 0)
        {
            bare = bare[..suffix];
        }

        // "0.2" alone is not a version this app ever writes, so it is unreadable, not lenient.
        return Version.TryParse(bare, out var parsed) && parsed.Build >= 0 ? parsed : null;
    }
}
