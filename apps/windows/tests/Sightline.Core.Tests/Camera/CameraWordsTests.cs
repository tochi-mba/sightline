using Shouldly;
using Sightline.Core.Camera;
using Sightline.Protocol.GpSock;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>What the window says about the camera.</summary>
public sealed class CameraWordsTests
{
    private static readonly Problem Lost = new(ProblemKind.Lost, "gone");
    private static readonly CameraState Connected = CameraState.Initial with { Connection = Connection.Connected.Instance };
    private static readonly CameraStatus Idle = new(CameraMode.Record, false, false, null, TimeSpan.FromSeconds(3725), 1234);

    [Fact]
    public void The_connection_has_one_word_or_none()
    {
        CameraWords.Connection(Connection.Idle.Instance).ShouldBeNull();
        CameraWords.Connection(Connection.Joining.Instance).ShouldBe("Connecting");
        CameraWords.Connection(Connection.Opening.Instance).ShouldBe("Connecting");
        CameraWords.Connection(Connection.Connected.Instance).ShouldBe("Connected");
        CameraWords.Connection(new Connection.Reconnecting(1, 5, Lost)).ShouldBe("Reconnecting");
        CameraWords.Connection(new Connection.Failed(Lost)).ShouldBe("Not connected");
    }

    [Fact]
    public void The_picture_says_why_it_is_not_playing()
    {
        CameraWords.Picture(Connected with { Connection = new Connection.Reconnecting(2, 4, Lost) })
            .ShouldBe("Reconnecting to the camera, attempt 2 of 4");
        CameraWords.Picture(Connected with { Live = new LiveView.Interrupted("x") }).ShouldBe("The camera stopped sending its picture. Waiting for it.");
        CameraWords.Picture(Connected with { Live = new LiveView.Unavailable("x") })
            .ShouldBe("No live picture until the camera is switched off and on. It gives one each time it starts.");
        CameraWords.Picture(Connected with { Live = LiveView.Starting.Instance }).ShouldBe("Starting the picture");
        CameraWords.Picture(Connected with { Live = LiveView.Paused.Instance }).ShouldBe("The card is being read");
        CameraWords.Picture(Connected with { Live = new LiveView.Playing(12) }).ShouldBeNull();
    }

    [Fact]
    public void The_shutter_says_what_it_does()
    {
        CameraWords.Shutter(Connected).ShouldBe("Start recording");
        CameraWords.Shutter(Connected with { Mode = CaptureMode.Photo }).ShouldBe("Take a photo");
        CameraWords.Shutter(Connected with { Status = Idle with { IsRecording = true } }).ShouldBe("Stop recording");
    }

    [Fact]
    public void The_status_line_says_what_the_card_still_holds_and_the_power()
    {
        CameraWords.Status(Connected).ShouldBe("");
        CameraWords.Status(Connected with { Status = Idle }).ShouldBe("Room for 1:02:05 more video");
        CameraWords.Status(Connected with { Status = Idle, Mode = CaptureMode.Photo }).ShouldBe("Room for 1234 more photos");
        CameraWords.Status(Connected with { Status = Idle with { RecordTimeLeft = null } }).ShouldBe("");
        CameraWords.Status(Connected with { Status = Idle with { PhotosLeft = null }, Mode = CaptureMode.Photo }).ShouldBe("");
        CameraWords.Status(Connected with { Status = Idle with { IsRecording = true, OnExternalPower = true } })
            .ShouldBe("Recording to the camera's card  ·  On USB power");
        CameraWords.Status(Connected with { Status = Idle, Task = CameraTask.ReadingCard }).ShouldBe("Reading the card");
    }

    [Fact]
    public void Every_task_has_its_words()
    {
        Enum.GetValues<CameraTask>().Select(CameraWords.Task).ShouldBe([
            "Taking a photo", "Starting to record", "Stopping the recording", "Switching mode",
            "Changing a setting", "Reading the card", "Copying from the card", "Deleting from the card"]);
    }

    [Fact]
    public void Lengths_read_as_a_clock()
    {
        CameraWords.Clock(TimeSpan.FromSeconds(83)).ShouldBe("1:23");
        CameraWords.Clock(TimeSpan.FromSeconds(5)).ShouldBe("0:05");
        CameraWords.Clock(TimeSpan.FromSeconds(3725)).ShouldBe("1:02:05");
    }

    [Fact]
    public void A_state_is_required()
    {
        Should.Throw<ArgumentNullException>(() => CameraWords.Picture(null!));
        Should.Throw<ArgumentNullException>(() => CameraWords.Shutter(null!));
        Should.Throw<ArgumentNullException>(() => CameraWords.Status(null!));
    }
}
