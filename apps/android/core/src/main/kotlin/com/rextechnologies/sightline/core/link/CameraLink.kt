package com.rextechnologies.sightline.core.link

import com.rextechnologies.sightline.protocol.CameraSockets
import java.io.Closeable

/**
 * Which camera network to join.
 *
 * The first time, only the family's name prefix is known, and the phone's own picker lists every camera
 * that matches so the person taps theirs. After that the camera's exact name is known, read from the
 * camera itself, and a request for exactly that network can be answered without asking again.
 *
 * @property name The camera's exact Wi-Fi name, or null to ask for any network starting [prefix].
 * @property prefix What every camera of this family's Wi-Fi name starts with.
 * @property password The camera's Wi-Fi password. Never logged.
 */
data class CameraNetwork(
    val name: String? = null,
    val prefix: String = DEFAULT_PREFIX,
    val password: String = DEFAULT_PASSWORD,
) {
    init {
        require(name == null || name.isNotBlank()) { "A camera's Wi-Fi name cannot be blank." }
        require(prefix.isNotBlank()) { "The name prefix cannot be blank." }
        require(password.length in 8..63 && password.all { it in ' '..'~' }) {
            "A Wi-Fi password is 8 to 63 printable characters."
        }
    }

    /** Whether this asks for one particular camera rather than any that matches the prefix. */
    val isExact: Boolean get() = name != null

    /** The same network, asking for the camera called [name] from now on. */
    fun exactly(name: String): CameraNetwork = copy(name = name)

    /** Never the password: a network description ends up in logs and diagnostics. */
    override fun toString(): String = "CameraNetwork(${name ?: "$prefix*"})"

    companion object {
        /** The prefix the reference camera's Wi-Fi name starts with, as its own screen shows it. */
        const val DEFAULT_PREFIX = "ActionCam_"

        /** The password the reference camera ships with, as its own screen shows it. */
        const val DEFAULT_PASSWORD = "12345678"
    }
}

/**
 * Joins the camera's Wi-Fi without taking the phone off its internet connection.
 *
 * On Android this is a local-only network request: the camera network carries only the sockets made
 * from it, and the default route stays on mobile data or whatever else had it. The seam exists so the
 * session above it is tested without a phone, and so nothing above it can reach for a call that would
 * take the phone offline.
 */
fun interface CameraLink {
    /**
     * Joins [network], waiting while the system asks the person where it must.
     *
     * Cancelling gives the request up and leaves the camera's network, so a person who leaves the screen
     * mid-join is not left on it.
     *
     * @throws CameraLinkException The system could not or would not join it.
     */
    suspend fun join(network: CameraNetwork): CameraLinkLease
}

/**
 * The camera's network, held until [close] gives it back: connections and sockets carried over the camera's
 * network and nothing else.
 */
interface CameraLinkLease : CameraSockets, Closeable {
    /** Returns once the system has lost the camera's network, which a sleeping camera causes. */
    suspend fun awaitLoss()

    /** Leaves the camera's network; the phone goes back to whatever it was on. Harmless to repeat. */
    override fun close()
}

/** Why the camera's network could not be joined. */
enum class LinkFailure {
    /**
     * Nothing was joined: no matching network was in range, the person dismissed the system's
     * request, or it timed out. Android reports all three the same way, so they are said together.
     */
    Unavailable,

    /** The phone's Wi-Fi is switched off. */
    WifiOff,

    /** The app is not allowed to look for nearby Wi-Fi. */
    PermissionDenied,
}

/** The camera's network could not be joined, for [failure]; [message] says what the system reported. */
class CameraLinkException(val failure: LinkFailure, override val message: String) : Exception(message)
