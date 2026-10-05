using Sightline.Core.Connectivity;
using Sightline.Protocol.GpSock;
using Sightline.Protocol.Rtp;

namespace Sightline.Core.Camera;

/// <summary>What the person can do about a <see cref="Problem"/>; the window turns each into one button.</summary>
public enum Remedy
{
    /// <summary>Try again, after doing what the explanation says.</summary>
    TryAgain,

    /// <summary>Pick a different Wi-Fi adapter, or another camera, and connect again.</summary>
    ChooseAgain,
}

/// <summary>
/// Why the camera could not be reached or stopped answering: one sentence and one remedy each, the same
/// table the Android app uses, so a failure reads the same on both.
/// </summary>
public enum ProblemKind
{
    /// <summary>This PC did not join the camera's Wi-Fi.</summary>
    NotJoined,

    /// <summary>Joined, but the camera did not answer.</summary>
    NoAnswer,

    /// <summary>The camera went away.</summary>
    Lost,

    /// <summary>Anything else.</summary>
    Unexpected,
}

/// <summary>A failure, as the person sees it.</summary>
/// <param name="Kind">Which explanation and remedy apply.</param>
/// <param name="Detail">What actually happened, for the diagnostics; never shown as the explanation.</param>
public sealed record Problem(ProblemKind Kind, string Detail)
{
    /// <summary>The sentence to show.</summary>
    public string Explanation => Kind switch
    {
        ProblemKind.NotJoined => "This PC did not join the camera's Wi-Fi. Make sure its Wi-Fi is on and it is close by, then try again.",
        ProblemKind.NoAnswer => "This PC joined the camera's Wi-Fi but the camera did not answer. Another app may be connected to it: close it and try again.",
        ProblemKind.Lost => "The camera's Wi-Fi went away. It may have gone to sleep or out of range; wake it and try again.",
        _ => "Something went wrong talking to the camera.",
    };

    /// <summary>What the person can do.</summary>
    public Remedy Remedy => Kind == ProblemKind.NotJoined ? Remedy.ChooseAgain : Remedy.TryAgain;

    /// <summary>The problem <paramref name="failure"/> amounts to while joining the camera's network.</summary>
    public static Problem WhileJoining(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(ProblemKind.NotJoined, failure.Message);
    }

    /// <summary>
    /// The problem <paramref name="failure"/> amounts to once on the camera's network. A camera that never
    /// answers or drops the socket reads as not answering; one that closes the channel itself, as a sleeping
    /// camera does, reads as lost; anything else, a refusal included, is unexpected.
    /// </summary>
    public static Problem WhileTalking(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var kind = failure switch
        {
            TimeoutException or IOException or System.Net.Sockets.SocketException or CameraNotReachableException => ProblemKind.NoAnswer,
            GpSockProtocolException or RtspException => ProblemKind.Lost,
            CameraLinkException or WlanException => ProblemKind.NotJoined,
            _ => ProblemKind.Unexpected,
        };
        return new(kind, failure.Message);
    }
}
