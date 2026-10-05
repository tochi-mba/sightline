using Sightline.Core.Updates;
using Shouldly;
using Xunit;

namespace Sightline.Core.Tests.Updates;

public sealed class WhatsNewTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    public void Numbers_rejects_values_the_app_never_writes(string? value) =>
        WhatsNewCatalog.Numbers(value).ShouldBeNull();

    [Theory]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("2.14.7-rolling.12", 2, 14, 7)]
    [InlineData("3.1.4+g1abc", 3, 1, 4)]
    [InlineData("5.0.0-preview.2+sha", 5, 0, 0)]
    public void Numbers_compares_the_human_version_not_build_labels(
        string value, int major, int minor, int build) =>
        WhatsNewCatalog.Numbers(value).ShouldBe(new Version(major, minor, build));

    [Fact]
    public void A_first_install_has_no_update_notes() =>
        WhatsNewCatalog.Since(null, "0.4.0", Notes()).ShouldBeEmpty();

    [Theory]
    [InlineData("broken", "0.4.0")]
    [InlineData("0.2.0", "broken")]
    [InlineData("0.2.0", "0.2.0-rolling.17")]
    [InlineData("0.4.0", "0.3.0")]
    public void Unreadable_same_and_downgraded_versions_have_no_notes(string previous, string current) =>
        WhatsNewCatalog.Since(previous, current, Notes()).ShouldBeEmpty();

    [Fact]
    public void Since_returns_only_crossed_versions_newest_first_and_skips_bad_entries()
    {
        var notes = Notes().Append(new WhatsNewEntry("bad", ["Never shown."])).ToList();

        var result = WhatsNewCatalog.Since("0.1.0", "0.3.5+sha", notes);

        result.Select(entry => entry.Version).ShouldBe(["0.3.0", "0.2.0"]);
    }

    [Fact]
    public void The_initial_curated_catalogue_is_deliberately_empty() =>
        WhatsNewCatalog.Entries.ShouldBeEmpty();

    private static IReadOnlyList<WhatsNewEntry> Notes() =>
    [
        new("0.2.0", ["Two."]),
        new("0.4.0", ["Four."]),
        new("0.3.0", ["Three."]),
    ];

    [Fact]
    public void Without_notes_given_the_curated_ones_are_used()
    {
        WhatsNewCatalog.Since("0.0.1", "99.0.0").ShouldBe(WhatsNewCatalog.Entries);
    }
}
