using Shouldly;
using Sightline.Core.Camera;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>What the window says about the files on the card.</summary>
public sealed class CardWordsTests
{
    private static readonly DateTime Sunday = new(2026, 10, 4, 18, 35, 0);

    private static CameraFile File(char code, DateTime? taken, long kilobytes = 3, int index = 1) => new(code, index, taken, kilobytes);

    [Fact]
    public void Files_are_grouped_by_day_newest_first_with_undated_ones_last()
    {
        var days = CardWords.ByDay([
            File('J', Sunday, index: 1),
            File('A', null, index: 2),
            File('J', Sunday.AddDays(1), index: 3),
            File('J', Sunday.AddHours(1), index: 4),
        ]);

        days.Select(d => d.Day).ShouldBe(["Monday 5 October 2026", "Sunday 4 October 2026", "Date unknown"]);
        days[1].Files.Select(f => f.Index).ShouldBe([4, 1]);
        CardWords.ByDay([File('J', Sunday)]).Select(d => d.Day).ShouldBe(["Sunday 4 October 2026"]);
    }

    [Fact]
    public void The_heading_counts_videos_photos_and_anything_else()
    {
        CardWords.Count([]).ShouldBe("Nothing on the card");
        CardWords.Count([File('A', Sunday), File('J', Sunday)]).ShouldBe("1 video, 1 photo");
        CardWords.Count([File('J', Sunday), File('J', Sunday), File('X', Sunday)]).ShouldBe("2 photos, 1 other file");
        CardWords.Count([File('L', Sunday), File('S', Sunday), File('X', Sunday), File('X', Sunday)]).ShouldBe("2 videos, 2 other files");
    }

    [Fact]
    public void Every_kind_has_a_word()
    {
        Enum.GetValues<CameraFileKind>().Select(CardWords.Kind).ShouldBe(
            ["Photo", "Video", "Locked video", "Emergency video", "File"], ignoreOrder: true);
    }

    [Fact]
    public void Sizes_read_as_people_say_them()
    {
        CardWords.Size(295 * 1024).ShouldBe("295 KB");
        CardWords.Size(1).ShouldBe("1 KB");
        CardWords.Size((long)(9.1 * 1024 * 1024)).ShouldBe("9.1 MB");
        CardWords.Size((long)(2.3 * 1024 * 1024 * 1024)).ShouldBe("2.3 GB");
    }

    [Fact]
    public void A_file_is_described_by_its_kind_date_and_size()
    {
        CardWords.Describe(File('J', Sunday)).ShouldBe("Photo, 4 October 2026, 18:35, 3 KB");
        CardWords.Describe(File('A', null, 2048)).ShouldBe("Video, 2.0 MB");
    }

    [Fact]
    public void A_copy_says_where_it_is()
    {
        CardWords.Transfer(Transfer.Queued.Instance).ShouldBe("Waiting to copy");
        CardWords.Transfer(new Transfer.Copying(450, 1000)).ShouldBe("Copying, 45%");
        CardWords.Transfer(new Transfer.Copying(1200, 1000)).ShouldBe("Copying, 100%");
        CardWords.Transfer(new Transfer.Copying(10, 0)).ShouldBe("Copying");
        CardWords.Transfer(new Transfer.Saved(@"C:\x.jpg")).ShouldBe("On this PC");
        CardWords.Transfer(new Transfer.Failed("The camera said no.")).ShouldBe("The camera said no.");
    }

    [Fact]
    public void Nothing_is_described_from_nothing()
    {
        Should.Throw<ArgumentNullException>(() => CardWords.ByDay(null!));
        Should.Throw<ArgumentNullException>(() => CardWords.Count(null!));
        Should.Throw<ArgumentNullException>(() => CardWords.Describe(null!));
        Should.Throw<ArgumentNullException>(() => CardWords.Transfer(null!));
    }
}
