using System.Buffers.Binary;
using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>A page of the camera's file list, read the way the firmware writes it.</summary>
public sealed class CameraFileTests
{
    [Theory]
    [InlineData('J', CameraFileKind.Photo, "PICT0007", true, false)]
    [InlineData('A', CameraFileKind.Video, "MOVI0007", false, true)]
    [InlineData('V', CameraFileKind.Video, "MOVI0007", false, true)]
    [InlineData('L', CameraFileKind.ProtectedVideo, "LOCK0007", false, true)]
    [InlineData('K', CameraFileKind.ProtectedVideo, "LOCK0007", false, true)]
    [InlineData('S', CameraFileKind.EmergencyVideo, "SOS0007", false, true)]
    [InlineData('O', CameraFileKind.EmergencyVideo, "SOS0007", false, true)]
    [InlineData('X', CameraFileKind.Other, "FILE0007", false, false)]
    public void The_type_letter_says_what_kind_of_file_it_is(
        char code, CameraFileKind kind, string name, bool photo, bool video)
    {
        var file = CameraFile.ParsePage(Page(Entry(code, 7, 24, 1, 31, 9, 15, 0, 2048)))[0];

        file.Kind.ShouldBe(kind);
        file.DisplayName.ShouldBe(name);
        file.IsPhoto.ShouldBe(photo);
        file.IsVideo.ShouldBe(video);
    }

    [Fact]
    public void An_entry_carries_its_index_the_time_it_was_taken_and_its_size()
    {
        var file = CameraFile.ParsePage(Page(Entry('J', 513, 24, 1, 31, 9, 15, 42, 3100)))[0];

        file.Index.ShouldBe(513);
        file.Taken.ShouldBe(new DateTime(2024, 1, 31, 9, 15, 42));
        file.SizeKilobytes.ShouldBe(3100);
        file.ApproximateBytes.ShouldBe(3100L * 1024);
    }

    [Fact]
    public void An_impossible_date_is_unknown_rather_than_invented()
    {
        // The camera's clock is only as right as somebody last set it, and a month of 13 is not a
        // date anyone should see.
        var file = CameraFile.ParsePage(Page(Entry('J', 1, 24, 13, 1, 0, 0, 0, 1)))[0];

        file.Taken.ShouldBeNull();
    }

    [Fact]
    public void Several_entries_on_a_page_are_read_in_order()
    {
        var files = CameraFile.ParsePage(Page(
            Entry('J', 1, 25, 6, 1, 10, 0, 0, 10),
            Entry('A', 2, 25, 6, 1, 10, 5, 0, 90_000),
            Entry('L', 3, 25, 6, 1, 10, 9, 0, 45_000)));

        files.Select(f => f.Index).ShouldBe([1, 2, 3]);
        files.Select(f => f.Kind).ShouldBe([CameraFileKind.Photo, CameraFileKind.Video, CameraFileKind.ProtectedVideo]);
    }

    [Fact]
    public void Entries_longer_than_the_documented_thirteen_bytes_are_still_read()
    {
        // The entry size is the page length divided by the count, so a firmware that appends a
        // field of its own does not break the fields before it.
        var longer = Entry('J', 4, 25, 2, 3, 4, 5, 6, 77).Concat(new byte[3]).ToArray();
        var second = Entry('A', 5, 25, 2, 3, 4, 5, 7, 88).Concat(new byte[3]).ToArray();

        var files = CameraFile.ParsePage(Page(longer, second));

        files.Count.ShouldBe(2);
        files[1].Index.ShouldBe(5);
        files[1].SizeKilobytes.ShouldBe(88);
    }

    [Fact]
    public void An_empty_page_has_no_files()
    {
        CameraFile.ParsePage([]).ShouldBeEmpty();
        CameraFile.ParsePage([0]).ShouldBeEmpty();
    }

    [Fact]
    public void A_page_that_does_not_divide_into_whole_entries_is_reported()
    {
        var ragged = new byte[1 + 25];
        ragged[0] = 2;

        Should.Throw<GpSockProtocolException>(() => CameraFile.ParsePage(ragged));
    }

    [Fact]
    public void A_page_whose_entries_are_too_short_to_hold_the_fields_is_reported()
    {
        var short_entries = new byte[1 + 12];
        short_entries[0] = 1;

        Should.Throw<GpSockProtocolException>(() => CameraFile.ParsePage(short_entries));
    }

    private static byte[] Page(params byte[][] entries) =>
        [(byte)entries.Length, .. entries.SelectMany(e => e)];

    private static byte[] Entry(char code, int index, int year, int month, int day, int hour, int minute, int second, uint kilobytes)
    {
        var entry = new byte[CameraFile.MinimumEntryLength];
        entry[0] = (byte)code;
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(1), (ushort)index);
        entry[3] = (byte)year;
        entry[4] = (byte)month;
        entry[5] = (byte)day;
        entry[6] = (byte)hour;
        entry[7] = (byte)minute;
        entry[8] = (byte)second;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(9), kilobytes);
        return entry;
    }
}
