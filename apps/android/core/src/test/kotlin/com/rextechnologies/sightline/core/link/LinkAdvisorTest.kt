package com.rextechnologies.sightline.core.link

import kotlin.test.Test
import kotlin.test.assertEquals

/** The plan's coexistence matrix, one row a test: what connecting does to the phone's other connections. */
class LinkAdvisorTest {
    private val mobileOnly = PhoneNetworks(
        wifiEnabled = true,
        onWifi = false,
        mobileData = true,
        sideBySideWifi = false,
        hotspotOn = false,
        hotspotAlongsideWifi = true,
        vpnOn = false,
    )

    @Test
    fun `wifi switched off blocks the camera and says mobile data is safe`() {
        val advice = LinkAdvisor.advise(mobileOnly.copy(wifiEnabled = false))

        assertEquals(AdviceLevel.Blocked, advice.level)
        assertEquals(
            listOf("Wi-Fi is off. Turn it on to reach the camera; mobile data stays on either way."),
            advice.sentences,
        )
    }

    @Test
    fun `on mobile data alone nothing changes`() {
        val advice = LinkAdvisor.advise(mobileOnly)

        assertEquals(AdviceLevel.Fine, advice.level)
        assertEquals(listOf("Mobile data keeps you online while the camera is connected."), advice.sentences)
    }

    @Test
    fun `on wifi with mobile data the wifi pauses and mobile data carries on`() {
        val advice = LinkAdvisor.advise(mobileOnly.copy(onWifi = true))

        assertEquals(AdviceLevel.Note, advice.level)
        assertEquals(
            listOf("Your Wi-Fi pauses while the camera is connected. Mobile data keeps you online."),
            advice.sentences,
        )
    }

    @Test
    fun `on wifi with mobile data off the person is warned they will be offline`() {
        val advice = LinkAdvisor.advise(mobileOnly.copy(onWifi = true, mobileData = false))

        assertEquals(AdviceLevel.Warning, advice.level)
        assertEquals(
            listOf(
                "Your Wi-Fi pauses while the camera is connected. With mobile data off, you are offline until you disconnect.",
            ),
            advice.sentences,
        )
    }

    @Test
    fun `a phone that can hold two wifi networks keeps its own`() {
        val advice = LinkAdvisor.advise(mobileOnly.copy(onWifi = true, mobileData = false, sideBySideWifi = true))

        assertEquals(AdviceLevel.Fine, advice.level)
        assertEquals(listOf("The camera joins alongside your Wi-Fi, so you stay online."), advice.sentences)
    }

    @Test
    fun `with nothing online the camera still works`() {
        val advice = LinkAdvisor.advise(mobileOnly.copy(mobileData = false))

        assertEquals(AdviceLevel.Fine, advice.level)
        assertEquals(listOf("The camera works without an internet connection."), advice.sentences)
    }

    @Test
    fun `a hotspot stays on where the phone can keep it, and is warned about where it cannot`() {
        val keeps = LinkAdvisor.advise(mobileOnly.copy(hotspotOn = true))
        val stops = LinkAdvisor.advise(mobileOnly.copy(hotspotOn = true, hotspotAlongsideWifi = false))

        assertEquals(AdviceLevel.Fine, keeps.level)
        assertEquals("Your hotspot stays on.", keeps.sentences.last())
        assertEquals(AdviceLevel.Warning, stops.level)
        assertEquals("Your hotspot stops while the camera is connected.", stops.sentences.first())
    }

    @Test
    fun `a vpn keeps protecting everything else`() {
        val advice = LinkAdvisor.advise(mobileOnly.copy(vpnOn = true))

        assertEquals(AdviceLevel.Fine, advice.level)
        assertEquals(
            "Your VPN keeps protecting everything else; only the camera is reached directly.",
            advice.sentences.last(),
        )
    }

    @Test
    fun `the most serious sentence comes first and sets the level`() {
        val advice = LinkAdvisor.advise(
            mobileOnly.copy(onWifi = true, hotspotOn = true, hotspotAlongsideWifi = false, vpnOn = true),
        )

        assertEquals(AdviceLevel.Warning, advice.level)
        assertEquals(3, advice.sentences.size)
        assertEquals("Your hotspot stops while the camera is connected.", advice.sentences[0])
    }
}
