using Shouldly;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Protocol.Tests;

/// <summary>The control channel, against a camera that behaves the way the real one did.</summary>
public sealed class GpSockConnectionTests
{
    private static async Task<(GpSockConnection Connection, FakeCamera Camera)> OpenAsync(
        Action<FakeCamera>? configure = null)
    {
        var camera = new FakeCamera();
        configure?.Invoke(camera);
        var connection = new GpSockConnection(camera);
        await connection.OpenAsync(CancellationToken.None);
        return (connection, camera);
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
    public async Task Browsing_the_card_before_switching_mode_is_refused_with_a_reason()
    {
        var (connection, _) = await OpenAsync();

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.GetFileCountAsync(CancellationToken.None));

        refused.Reason.ShouldBe(NakCode.ServerBusy);
        refused.Message.ShouldContain("busy");
    }

    [Fact]
    public async Task Browsing_works_once_the_camera_is_in_browse_mode()
    {
        var (connection, _) = await OpenAsync(c => c.FileCount = 7);

        await connection.SetModeAsync(CameraMode.Browse, CancellationToken.None);
        var count = await connection.GetFileCountAsync(CancellationToken.None);

        count.ShouldBe(7);
    }

    [Fact]
    public async Task Writing_a_setting_sends_the_id_and_the_value()
    {
        var (connection, camera) = await OpenAsync();

        await connection.SetSettingAsync(0x0100, 3, CancellationToken.None);

        camera.SettingsWritten.ShouldBe([(0x0100, 3)]);
    }

    [Fact]
    public async Task A_command_the_camera_does_not_know_is_reported_as_such()
    {
        var (connection, _) = await OpenAsync();

        var refused = await Should.ThrowAsync<GpSockRefusedException>(
            () => connection.DemandAsync(GpSockCommand.PowerOff, default, CancellationToken.None));

        refused.Reason.ShouldBe(NakCode.InvalidCommand);
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
        var (connection, camera) = await OpenAsync(c => c.FileCount = 4);
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
    public async Task Every_refusal_code_turns_into_a_sentence_rather_than_a_number()
    {
        foreach (var code in Enum.GetValues<NakCode>())
        {
            var explained = GpSockRefusedException.Explain(code);
            explained.ShouldNotBeNullOrWhiteSpace();
            explained.ShouldNotContain("reason ");
        }

        await Task.CompletedTask;
    }
}
