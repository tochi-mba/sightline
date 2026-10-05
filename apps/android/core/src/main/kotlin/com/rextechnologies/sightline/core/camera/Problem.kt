package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.core.session.CameraTimeoutException
import com.rextechnologies.sightline.protocol.gpsock.GpSockProtocolException
import java.io.IOException

/**
 * What the person can do about a [Problem]. The app turns each into one button.
 */
enum class Remedy {
    /** Try again, after doing what the explanation says. */
    TryAgain,

    /** Open the phone's Wi-Fi settings panel. */
    OpenWifiSettings,

    /** Open this app's permissions, to allow nearby Wi-Fi. */
    OpenAppPermissions,
}

/**
 * Why the camera could not be reached or stopped answering, in one sentence a person can act on.
 *
 * Each kind has exactly one explanation and one remedy, the same table the plan's connection doctor
 * sets out, so the same failure always reads the same way wherever it is shown.
 */
enum class ProblemKind(val explanation: String, val remedy: Remedy) {
    WifiOff(
        "Wi-Fi is off. The camera is reached over Wi-Fi, and turning it on does not take you off mobile data.",
        Remedy.OpenWifiSettings,
    ),
    PermissionDenied(
        "Sightline needs the Nearby devices permission to find the camera's Wi-Fi.",
        Remedy.OpenAppPermissions,
    ),
    NotJoined(
        "The phone did not join the camera. Make sure its Wi-Fi is on and it is close by, " +
            "then tap the camera when Android asks.",
        Remedy.TryAgain,
    ),
    NoAnswer(
        "The phone joined the camera's Wi-Fi but the camera did not answer. " +
            "Another app may be connected to it: close it and try again.",
        Remedy.TryAgain,
    ),
    Lost(
        "The camera's Wi-Fi went away. It may have gone to sleep or out of range; wake it and try again.",
        Remedy.TryAgain,
    ),
    Unexpected("Something went wrong talking to the camera.", Remedy.TryAgain),
}

/**
 * A failure, as the person sees it.
 *
 * @property kind Which explanation and remedy apply.
 * @property detail What actually happened, for the diagnostics page; never shown as the explanation.
 */
data class Problem(val kind: ProblemKind, val detail: String) {
    /** The sentence to show. */
    val explanation: String get() = kind.explanation

    companion object {
        /** The problem [failure] amounts to while joining the camera's network. */
        fun whileJoining(failure: CameraLinkException): Problem = Problem(
            when (failure.failure) {
                LinkFailure.Unavailable -> ProblemKind.NotJoined
                LinkFailure.WifiOff -> ProblemKind.WifiOff
                LinkFailure.PermissionDenied -> ProblemKind.PermissionDenied
            },
            failure.message,
        )

        /**
         * The problem [failure] amounts to once on the camera's network.
         *
         * A camera that never answers or drops the socket reads as not answering; one that closes the
         * channel itself, as a sleeping camera does, reads as lost. Anything else, a refusal included, is
         * a camera that is there and said no, which is unexpected while connecting.
         */
        fun whileTalking(failure: Throwable): Problem = Problem(
            when (failure) {
                is CameraTimeoutException, is IOException -> ProblemKind.NoAnswer
                is GpSockProtocolException -> ProblemKind.Lost
                else -> ProblemKind.Unexpected
            },
            failure.message ?: failure.javaClass.simpleName,
        )
    }
}
