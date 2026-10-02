using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>The control channel, against a camera that behaves the way the real one did.</summary>
public sealed class GpSockConnectionTests
{
    private static readonly DateTime Taken = new(2026, 10, 2, 14, 30, 0);

    private static async Task<(GpSockConnection Connection, FakeCamera Camera)> OpenAsync(
        Action<FakeCamera>? configure = null)
    {
        var camera = new FakeCamera();
        configure?.Invoke(camera);
        var connection = new GpSockConnection(camera);
        await connection.OpenAsync(CancellationToken.None);
        return (connection, camera);
    }

    private static void AddFiles(FakeCamera camera, int count, int size = 100)
    {
        for (var i = 0; i < count; i++)
        {
            camera.AddFile(i % 2 == 0 ? 'J' : 'A', Taken.AddMinutes(i), Enumerable.Repeat((byte)i, size).ToArray());
        }
    }

    [Fact]
    public async Task It_reads_the_camera_state()
    {
        var (connection, _) = await OpenAsync();

        var status = await connection.GetStatusAsync(CancellationToken.None);

        status.Mode.ShouldBe(CameraMode.Record);
        status.Length.ShouldBe(16);
    }

    [Fact]
    public async Task It_gathers_a_chunked_answer_into_one_document()
    {
        // The menu is far longer than the camera's 242-byte chunk, so this is the path that
        // silently truncated everything if a reader assumed one frame per answer.
        var long_menu = new string(' ', 2000);
        var (connection, _) = await OpenAsync(c => c.MenuXml =
            $"<Menu><Categories><Category><Name>Record</Name><!--{long_menu}--><Settings>"
            + "<Setting><Name>Resolution</Name><ID>0x0</ID><Type>0x00</Type><Default>0x0</Default>"
            + "<Values><Value><ID>0x00</ID><Name>4K</Name></Value></Values></Setting>"
            + "</Settings></Category></Categories></Menu>");

        var menu = await connection.GetMenuAsync(CancellationToken.None);

        menu.Settings.Count.ShouldBe(1);
        menu.Find(0)!.LabelFor(0).ShouldBe("4K");
    }

    [Fact]
    public async Task A_chunked_answer_reports_its_running_total()
    {
        var (connection, _) = await OpenAsync();
        var totals = new List<int>();

        var bytes = await connection.AskForChunksAsync(
            GpSockCommand.GetParameterFile, default, new Synchronous<int>(totals.Add), CancellationToken.None);

        totals.ShouldNotBeEmpty();
        totals[^1].ShouldBe(bytes.Length);
        totals.ShouldBeInOrder();
    }

    [Fact]
    public async Task A_frame_arriving_a_few_bytes_at_a_time_is_still_read_correctly()
    {
        // TCP does not promise frame boundaries, and this camera sends no length on requests, so
        // a reader that assumed one read per frame would work on a desk and fail on a bad link.
        var (connection, _) = await OpenAsync(c => c.DribbleBytes = 3);

        var status = await connection.GetStatusAsync(CancellationToken.None);

        status.Mode.ShouldBe(CameraMode.Record);
    }

    [Fact]
    public async Task Taking_a_photograph_tells_the_camera_to()
    {
        var (connection, camera) = await OpenAsync();

        await connection.CapturePictureAsync(CancellationToken.None);

        camera.PicturesTaken.ShouldBe(1);
    }

    [Fact]
    public async Task Recording_toggles_rather_than_taking_an_argument()
    {
        var (connection, camera) = await OpenAsync();

        await connection.ToggleRecordingAsync(CancellationToken.None);
        camera.IsRecording.ShouldBeTrue();

        await connection.ToggleRecordingAsync(CancellationToken.None);
        camera.IsRecording.ShouldBeFalse();
    }

    [Fact]
    public async Task Streaming_has_to_be_started_explicitly()
    {
        var (connection, camera) = await OpenAsync();
        camera.IsStreaming.ShouldBeFalse();

        await connection.StartStreamingAsync(CancellationToken.None);

        camera.IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public async Task Turning_the_camera_off_is_a_command_it_acknowledges()
    {
        var (connection, camera) = await OpenAsync();

        await connection.PowerOffAsync(CancellationToken.None);

        camera.IsPoweredOff.ShouldBeTrue();
    }

    [Fact]
    public async Task Browsing_the_card_before_switching_mode_is_refused_with_a_reason()
    {
        var (connection, _) = await OpenAsync(c => AddFiles(c, 2));

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.GetFileCountAsync(CancellationToken.None));

        refused.Reason.ShouldBe(NakCode.ServerBusy);
        refused.Message.ShouldContain("busy");
    }

    [Fact]
    public async Task Browsing_works_once_the_camera_is_in_browse_mode()
    {
        var (connection, _) = await OpenAsync(c => AddFiles(c, 7));

        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        var count = await connection.GetFileCountAsync(CancellationToken.None);

        count.ShouldBe(7);
    }

    [Fact]
    public async Task An_empty_card_counts_as_no_files_rather_than_an_error()
    {
        // The firmware refuses the count on an empty card with "no storage", exactly as it would
        // with no card at all; to a person both mean there is nothing to show.
        var (connection, _) = await OpenAsync();
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        (await connection.GetFileCountAsync(CancellationToken.None)).ShouldBe(0);
        (await connection.GetFileListAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_file_list_is_read_page_by_page_until_every_file_is_in()
    {
        var (connection, _) = await OpenAsync(c =>
        {
            AddFiles(c, 10);
            c.PageSize = 4;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        var files = await connection.GetFileListAsync(CancellationToken.None);

        files.Select(f => f.Index).ShouldBe(Enumerable.Range(1, 10));
        files[0].Kind.ShouldBe(CameraFileKind.Photo);
        files[1].Kind.ShouldBe(CameraFileKind.Video);
        files[3].Taken.ShouldBe(Taken.AddMinutes(3));
    }

    [Fact]
    public async Task A_camera_that_repeats_its_first_page_does_not_keep_the_app_paging_forever()
    {
        var (connection, _) = await OpenAsync(c =>
        {
            AddFiles(c, 10);
            c.PageSize = 4;
            c.RepeatsFirstPage = true;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        var files = await connection.GetFileListAsync(CancellationToken.None);

        files.Select(f => f.Index).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Listing_files_outside_browse_mode_is_refused()
    {
        var (connection, _) = await OpenAsync(c => AddFiles(c, 3));

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.GetFileListAsync(CancellationToken.None));

        refused.Reason.ShouldBe(NakCode.ServerBusy);
    }

    [Fact]
    public async Task A_thumbnail_comes_back_as_the_bytes_of_a_picture()
    {
        var (connection, _) = await OpenAsync(c => AddFiles(c, 2));
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        var thumbnail = await connection.GetThumbnailAsync(2, CancellationToken.None);

        thumbnail.ShouldBe(new byte[] { 0xFF, 0xD8, 2, 0xFF, 0xD9 });
    }

    [Fact]
    public async Task A_thumbnail_is_refused_while_the_camera_is_still_streaming()
    {
        var (connection, _) = await OpenAsync(c =>
        {
            AddFiles(c, 2);
            c.IsStreaming = true;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.GetThumbnailAsync(1, CancellationToken.None));

        refused.Reason.ShouldBe(NakCode.ServerBusy);
    }

    [Fact]
    public async Task A_file_index_outside_sixteen_bits_is_refused_before_it_is_sent()
    {
        var (connection, _) = await OpenAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => connection.GetThumbnailAsync(-1, CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => connection.DeleteFileAsync(65_536, CancellationToken.None));
    }

    [Fact]
    public async Task A_download_copies_the_whole_file_and_reports_its_progress()
    {
        var content = Enumerable.Range(0, 3500).Select(i => (byte)i).ToArray();
        var (connection, _) = await OpenAsync(c =>
        {
            c.AddFile('A', Taken, content);
            c.DownloadChunk = 1000;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        using var destination = new MemoryStream();
        var progress = new List<long>();

        var written = await connection.DownloadAsync(1, destination, new Synchronous<long>(progress.Add), CancellationToken.None);

        written.ShouldBe(3500);
        destination.ToArray().ShouldBe(content);
        progress.ShouldBe([1000L, 2000L, 3000L, 3500L]);
    }

    [Fact]
    public async Task A_download_needs_somewhere_to_put_the_file()
    {
        var (connection, _) = await OpenAsync();

        await Should.ThrowAsync<ArgumentNullException>(
            () => connection.DownloadAsync(1, null!, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_download_says_why_and_leaves_the_channel_usable()
    {
        var (connection, _) = await OpenAsync(c =>
        {
            AddFiles(c, 1);
            c.RefuseDownloadWith = NakCode.WriteFail;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        using var destination = new MemoryStream();

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.DownloadAsync(1, destination, null, CancellationToken.None));

        refused.Command.ShouldBe(GpSockCommand.PlaybackGetRawData);
        refused.Reason.ShouldBe(NakCode.WriteFail);
        (await connection.GetStatusAsync(CancellationToken.None)).Mode.ShouldBe(CameraMode.Browse);
    }

    [Fact]
    public async Task A_cancelled_download_leaves_the_channel_ready_for_the_next_command()
    {
        // The camera keeps sending until it sees another request. A client that simply stopped
        // reading would take the rest of the file as the answer to whatever it asked next.
        var (connection, camera) = await OpenAsync(c =>
        {
            c.AddFile('A', Taken, new byte[10_000]);
            c.DownloadChunk = 1000;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        using var destination = new MemoryStream();

        await Should.ThrowAsync<OperationCanceledException>(() => connection.DownloadAsync(
            1, destination, new Synchronous<long>(_ => cancel.Cancel()), cancel.Token));

        destination.Length.ShouldBe(1000);
        (await connection.GetStatusAsync(CancellationToken.None)).Mode.ShouldBe(CameraMode.Browse);
        (await connection.GetFileCountAsync(CancellationToken.None)).ShouldBe(1);
        connection.StaleFramesSkipped.ShouldBe(0);
        camera.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task A_destination_that_fails_mid_download_still_leaves_the_channel_in_step()
    {
        var (connection, _) = await OpenAsync(c =>
        {
            c.AddFile('A', Taken, new byte[5000]);
            c.DownloadChunk = 1000;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        using var full = new FullDisk(acceptWrites: 2);

        await Should.ThrowAsync<IOException>(() => connection.DownloadAsync(1, full, null, CancellationToken.None));

        (await connection.GetFileCountAsync(CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task A_camera_that_never_winds_down_a_cancelled_download_marks_the_channel_out_of_step()
    {
        var (connection, camera) = await OpenAsync(c =>
        {
            c.AddFile('A', Taken, new byte[4000]);
            c.DownloadChunk = 1000;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        using var destination = new MemoryStream();

        await Should.ThrowAsync<OperationCanceledException>(() => connection.DownloadAsync(
            1,
            destination,
            new Synchronous<long>(_ =>
            {
                // From here the camera ignores everything, so the request that should stop the
                // transfer is never answered.
                camera.HangsUp = true;
                cancel.Cancel();
            }),
            cancel.Token));

        var stuck = await Should.ThrowAsync<GpSockProtocolException>(
            () => connection.GetStatusAsync(CancellationToken.None));
        stuck.Message.ShouldContain("out of step");
    }

    [Fact]
    public async Task Deleting_a_file_removes_it_from_the_card()
    {
        var (connection, camera) = await OpenAsync(c => AddFiles(c, 3));
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        await connection.DeleteFileAsync(2, CancellationToken.None);

        camera.Files.Select(f => f.Index).ShouldBe([1, 3]);
    }

    [Fact]
    public async Task A_camera_built_without_delete_refuses_it()
    {
        var (connection, camera) = await OpenAsync(c =>
        {
            AddFiles(c, 1);
            c.SupportsDelete = false;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        await Should.ThrowAsync<GpSockRefusedException>(() => connection.DeleteFileAsync(1, CancellationToken.None));

        camera.Files.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Writing_a_setting_sends_the_id_and_the_value()
    {
        var (connection, camera) = await OpenAsync();

        await connection.SetSettingAsync(0x0100, 3, CancellationToken.None);

        camera.SettingsWritten.ShouldBe([(0x0100, 3)]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task A_choice_that_does_not_fit_in_a_byte_is_refused_before_it_is_sent(int value)
    {
        var (connection, camera) = await OpenAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => connection.SetSettingAsync(MenuIds.CaptureQuality, value, CancellationToken.None));

        camera.RawSettingsWritten.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_choice_setting_reads_back_as_its_value_id()
    {
        var (connection, _) = await OpenAsync(c => c.Values[MenuIds.CaptureResolution] = [3]);

        (await connection.GetChoiceAsync(MenuIds.CaptureResolution, CancellationToken.None)).ShouldBe(3);
    }

    [Fact]
    public async Task A_setting_the_firmware_does_not_know_reads_as_zero()
    {
        var (connection, _) = await OpenAsync();

        (await connection.GetChoiceAsync(0x7777, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task A_camera_that_sends_nothing_for_a_choice_is_reported()
    {
        var (connection, _) = await OpenAsync(c => c.Values[MenuIds.BeepSound] = []);

        await Should.ThrowAsync<GpSockProtocolException>(
            () => connection.GetChoiceAsync(MenuIds.BeepSound, CancellationToken.None));
    }

    [Fact]
    public async Task A_text_setting_reads_back_without_its_padding()
    {
        var (connection, _) = await OpenAsync();

        (await connection.GetTextAsync(MenuIds.WifiPassword, CancellationToken.None)).ShouldBe("12345678");
        (await connection.GetTextAsync(MenuIds.WifiName, CancellationToken.None)).ShouldBe("ActionCam_000000000000");
    }

    [Fact]
    public async Task Text_is_written_padded_to_the_size_of_the_cameras_field()
    {
        // The firmware copies the whole field whatever was sent, so a short write would let it
        // copy whatever followed in its buffer into the camera's password.
        var (connection, camera) = await OpenAsync();

        await connection.SetTextAsync(MenuIds.WifiPassword, "new-pass", FakeCamera.WifiFieldLength, CancellationToken.None);

        var (id, value) = camera.RawSettingsWritten.ShouldHaveSingleItem();
        id.ShouldBe(MenuIds.WifiPassword);
        value.ShouldBe(FakeCamera.PaddedField("new-pass", FakeCamera.WifiFieldLength));
        (await connection.GetTextAsync(MenuIds.WifiPassword, CancellationToken.None)).ShouldBe("new-pass");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a password far too long for this field")]
    [InlineData("pässword")]
    [InlineData("tab\there")]
    public async Task Text_the_camera_cannot_take_is_refused_before_it_is_sent(string value)
    {
        var (connection, camera) = await OpenAsync();

        await Should.ThrowAsync<ArgumentException>(
            () => connection.SetTextAsync(MenuIds.WifiPassword, value, 32, CancellationToken.None));

        camera.RawSettingsWritten.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_text_field_needs_a_real_size_and_real_text()
    {
        var (connection, _) = await OpenAsync();

        await Should.ThrowAsync<ArgumentNullException>(
            () => connection.SetTextAsync(MenuIds.WifiName, null!, 32, CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => connection.SetTextAsync(MenuIds.WifiName, "x", 0, CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => connection.SetTextAsync(MenuIds.WifiName, "x", 256, CancellationToken.None));
    }

    [Fact]
    public async Task A_command_the_camera_does_not_know_is_reported_as_such()
    {
        var (connection, _) = await OpenAsync();

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.DemandAsync(GpSockCommand.AuthDevice, default, CancellationToken.None));

        refused.Reason.ShouldBe(NakCode.InvalidCommand);
    }

    [Fact]
    public async Task A_leftover_answer_to_an_abandoned_request_is_skipped_not_taken_as_the_reply()
    {
        var (connection, camera) = await OpenAsync();
        camera.StrayFramesBeforeNextAnswer.Add(
            FakeCamera.Frame(GpSockType.Ack, GpSockCommand.PlaybackGetRawData, [1, 2, 3]));

        var status = await connection.GetStatusAsync(CancellationToken.None);

        status.Length.ShouldBe(16);
        connection.StaleFramesSkipped.ShouldBe(1);
    }

    [Fact]
    public async Task A_camera_that_hangs_up_is_reported_rather_than_waited_on_forever()
    {
        var camera = new FakeCamera { HangsUp = true };
        var connection = new GpSockConnection(camera);
        await connection.OpenAsync(CancellationToken.None);

        // The camera takes the command and never answers, which is what its access point going to
        // sleep mid-session looks like from here.
        await Should.ThrowAsync<GpSockProtocolException>(
            () => connection.AskAsync(GpSockCommand.GetDeviceStatus, default, CancellationToken.None));
    }

    [Fact]
    public async Task Commands_sent_at_the_same_moment_each_get_their_own_answer()
    {
        // The live view starts the stream while a person presses the shutter, both down one socket.
        // Each must read the answer to its own command, not the other's.
        var (connection, camera) = await OpenAsync(c => AddFiles(c, 4));
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);

        var asks = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? connection.AskAsync(GpSockCommand.GetDeviceStatus, default, CancellationToken.None)
            : connection.AskAsync(GpSockCommand.PlaybackGetFileCount, default, CancellationToken.None));
        var answers = await Task.WhenAll(asks);

        for (var i = 0; i < answers.Length; i++)
        {
            answers[i].Command.ShouldBe(i % 2 == 0 ? GpSockCommand.GetDeviceStatus : GpSockCommand.PlaybackGetFileCount);
        }

        camera.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task A_count_answer_too_short_to_hold_a_number_reads_as_no_files()
    {
        var (connection, camera) = await OpenAsync();
        camera.ForcedAnswers[GpSockCommand.PlaybackGetFileCount] = [7];

        (await connection.GetFileCountAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task A_page_with_more_files_than_the_count_stops_at_the_count()
    {
        // The count and the pages are separate answers, and a file can be written between them.
        // The count is what was asked about, so the list is held to it.
        var (connection, camera) = await OpenAsync(c =>
        {
            AddFiles(c, 4);
            c.PageSize = 4;
        });
        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        camera.ForcedAnswers[GpSockCommand.PlaybackGetFileCount] = [3, 0];

        var files = await connection.GetFileListAsync(CancellationToken.None);

        files.Select(f => f.Index).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Closing_the_channel_closes_the_connection_to_the_camera()
    {
        var (connection, camera) = await OpenAsync();

        await connection.DisposeAsync();

        camera.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public void A_channel_needs_a_transport()
    {
        Should.Throw<ArgumentNullException>(() => new GpSockConnection(null!));
    }

    [Fact]
    public void A_refusal_built_without_a_reason_reads_as_no_refusal()
    {
        new GpSockResponse(GpSockType.Nak, GpSockCommand.SetMode, []).Nak.ShouldBe(NakCode.Ok);
        new GpSockResponse(GpSockType.Ack, GpSockCommand.SetMode, [0xFF, 0xFF]).Nak.ShouldBe(NakCode.Ok);
    }

    [Fact]
    public void The_exceptions_carry_their_messages_and_causes()
    {
        var cause = new IOException("cause");

        new GpSockRefusedException().Message.ShouldNotBeNullOrWhiteSpace();
        new GpSockRefusedException("refused").Message.ShouldBe("refused");
        new GpSockRefusedException("refused", cause).InnerException.ShouldBe(cause);
        new GpSockProtocolException().Message.ShouldNotBeNullOrWhiteSpace();
        new GpSockProtocolException("broken", cause).InnerException.ShouldBe(cause);
    }

    [Fact]
    public async Task Every_refusal_code_turns_into_a_sentence_rather_than_a_number()
    {
        foreach (var code in Enum.GetValues<NakCode>())
        {
            var explained = GpSockRefusedException.Explain(code);
            explained.ShouldNotBeNullOrWhiteSpace();
            explained.ShouldNotContain("reason ");
        }

        GpSockRefusedException.Explain((NakCode)(-99)).ShouldBe("reason -99");
        await Task.CompletedTask;
    }

    /// <summary>Reports on the calling thread, so a test sees each report before the next chunk is read.</summary>
    private sealed class Synchronous<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>A destination that takes a few writes and then fails, as a full disk does.</summary>
    private sealed class FullDisk(int acceptWrites) : MemoryStream
    {
        private int writes;

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ++writes > acceptWrites ? throw new IOException("There is not enough space on the disk.") : base.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ++writes > acceptWrites ? throw new IOException("There is not enough space on the disk.") : base.WriteAsync(buffer, cancellationToken);
    }
}
