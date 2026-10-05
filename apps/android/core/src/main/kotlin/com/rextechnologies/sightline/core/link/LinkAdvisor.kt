package com.rextechnologies.sightline.core.link

/**
 * What the phone is doing with its networks, just before it joins the camera.
 *
 * @property wifiEnabled Whether the phone's Wi-Fi is switched on at all.
 * @property onWifi Whether the phone is connected to a Wi-Fi network now.
 * @property mobileData Whether mobile data is on and can reach the internet.
 * @property sideBySideWifi Whether this phone can stay on one Wi-Fi network while joining a second,
 *   local-only one: Android's station-and-station concurrency for local-only connections.
 * @property hotspotOn Whether the phone is sharing its connection as a Wi-Fi hotspot.
 * @property hotspotAlongsideWifi Whether this phone can keep a hotspot up while joined to a network.
 * @property vpnOn Whether a VPN is carrying the phone's traffic.
 */
data class PhoneNetworks(
    val wifiEnabled: Boolean,
    val onWifi: Boolean,
    val mobileData: Boolean,
    val sideBySideWifi: Boolean,
    val hotspotOn: Boolean,
    val hotspotAlongsideWifi: Boolean,
    val vpnOn: Boolean,
)

/** How much a person needs to read before connecting. */
enum class AdviceLevel {
    /** Nothing changes for them. */
    Fine,

    /** Something changes, and nothing is lost. */
    Note,

    /** They lose something while connected: their internet, or their hotspot. */
    Warning,

    /** The camera cannot be joined until they change something. */
    Blocked,
}

/**
 * What connecting will do to the phone's other connections, in sentences, most important first.
 *
 * @property level The most serious of them, which decides how prominently they are shown.
 */
data class LinkAdvice(val level: AdviceLevel, val sentences: List<String>)

/**
 * Says, before connecting, what joining the camera will do to the phone's internet.
 *
 * The point of Sightline is that connecting to the camera never quietly takes the phone offline. When a
 * phone can only be on one Wi-Fi network at a time and that network is its internet, joining the camera
 * pauses it; that cannot be fixed in software, so it is said before it happens rather than discovered
 * after. Every other case keeps the phone online, and says so.
 */
object LinkAdvisor {
    fun advise(phone: PhoneNetworks): LinkAdvice {
        if (!phone.wifiEnabled) {
            return LinkAdvice(
                AdviceLevel.Blocked,
                listOf("Wi-Fi is off. Turn it on to reach the camera; mobile data stays on either way."),
            )
        }

        val advice = mutableListOf<Pair<AdviceLevel, String>>()
        when {
            phone.onWifi && phone.sideBySideWifi ->
                advice += AdviceLevel.Fine to "The camera joins alongside your Wi-Fi, so you stay online."

            phone.onWifi && phone.mobileData ->
                advice += AdviceLevel.Note to WIFI_PAUSES + " Mobile data keeps you online."

            phone.onWifi ->
                advice += AdviceLevel.Warning to
                    WIFI_PAUSES + " With mobile data off, you are offline until you disconnect."

            phone.mobileData ->
                advice +=
                    AdviceLevel.Fine to "Mobile data keeps you online while the camera is connected."
            else -> advice += AdviceLevel.Fine to "The camera works without an internet connection."
        }

        if (phone.hotspotOn) {
            advice += if (phone.hotspotAlongsideWifi) {
                AdviceLevel.Fine to "Your hotspot stays on."
            } else {
                AdviceLevel.Warning to "Your hotspot stops while the camera is connected."
            }
        }

        if (phone.vpnOn) {
            advice += AdviceLevel.Fine to
                "Your VPN keeps protecting everything else; only the camera is reached directly."
        }

        val ordered = advice.sortedByDescending { it.first }
        return LinkAdvice(ordered.first().first, ordered.map { it.second })
    }

    private const val WIFI_PAUSES = "Your Wi-Fi pauses while the camera is connected."
}
