using System.Net.Sockets;
using Shouldly;
using Sightline.Core.Camera;
using Sightline.Core.Connectivity;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;
using Xunit;

namespace Sightline.Core.Tests.Camera;

/// <summary>What the window is told: problems in words, the camera's status, and the app's own failures.</summary>
public sealed class StateTests
{
    private static ProblemKind Kind(Exception failure) => Problem.WhileTalking(failure).Kind;

    [Fact]
    public void Each_problem_has_one_sentence_and_one_remedy()
    {
        new Problem(ProblemKind.NotJoined, "x").Explanation.ShouldStartWith("This PC did not join the camera's Wi-Fi.");
        new Problem(ProblemKind.NoAnswer, "x").Explanation.ShouldContain("Another app may be connected to it");
        new Problem(ProblemKind.Lost, "x").Explanation.ShouldContain("It may have gone to sleep");
        new Problem(ProblemKind.Unexpected, "x").Explanation.ShouldBe("Something went wrong talking to the camera.");

        new Problem(ProblemKind.NotJoined, "x").Remedy.ShouldBe(Remedy.ChooseAgain);
        new Problem(ProblemKind.NoAnswer, "x").Remedy.ShouldBe(Remedy.TryAgain);
    }

    [Fact]
    public void A_failure_is_sorted_by_what_it_says_about_the_camera()
    {
        Kind(new TimeoutException()).ShouldBe(ProblemKind.NoAnswer);
        Kind(new IOException()).ShouldBe(ProblemKind.NoAnswer);
        Kind(new SocketException()).ShouldBe(ProblemKind.NoAnswer);
        Kind(new CameraNotReachableException()).ShouldBe(ProblemKind.NoAnswer);
        Kind(new GpSockProtocolException()).ShouldBe(ProblemKind.Lost);
        Kind(new RtspException()).ShouldBe(ProblemKind.Lost);
        Kind(new CameraLinkException()).ShouldBe(ProblemKind.NotJoined);
        Kind(new WlanException()).ShouldBe(ProblemKind.NotJoined);
        Kind(new InvalidOperationException()).ShouldBe(ProblemKind.Unexpected);

        Problem.WhileTalking(new IOException("reset")).Detail.ShouldBe("reset");
        Problem.WhileJoining(new WlanException("busy")).ShouldBe(new Problem(ProblemKind.NotJoined, "busy"));
    }

    [Fact]
    public void A_status_is_shown_as_the_camera_sent_it_and_an_unknown_mode_as_none()
    {
        var bytes = Convert.FromHexString("000280010025b3000000fe7c0000a501");
        var sent = new DeviceStatus(bytes);
        var shown = CameraStatus.Of(sent);

        shown.Mode.ShouldBe(sent.Mode);
        shown.IsRecording.ShouldBe(sent.IsRecording);
        shown.OnExternalPower.ShouldBe(sent.OnExternalPower);
        shown.ClipLength.ShouldBe(sent.ClipLength);
        shown.RecordTimeLeft.ShouldBe(sent.RecordTimeLeft);
        shown.PhotosLeft.ShouldBe(sent.PhotosLeft);

        bytes[0] = 0x7F;
        CameraStatus.Of(new DeviceStatus(bytes)).Mode.ShouldBeNull();
    }

    [Fact]
    public void Before_any_status_the_camera_is_not_recording()
    {
        CameraState.Initial.IsRecording.ShouldBeFalse();
        CameraState.Initial.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void A_save_failure_keeps_what_the_disk_said()
    {
        var disk = new IOException("The disk is full.");

        new SaveFailureException("The disk is full.", disk).InnerException.ShouldBe(disk);
        new SaveFailureException("The disk is full.").Message.ShouldBe("The disk is full.");
        new SaveFailureException().Message.ShouldNotBeEmpty();
    }
}
