package com.rextechnologies.sightline.core.link

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class CameraNetworkTest {
    @Test
    fun `the defaults are the reference camera's, asking for any camera of the family`() {
        val network = CameraNetwork()

        assertEquals("ActionCam_", network.prefix)
        assertEquals("12345678", network.password)
        assertFalse(network.isExact)
    }

    @Test
    fun `once the camera's name is known the same network asks for exactly it`() {
        val exact = CameraNetwork(password = "secret-pass").exactly("ActionCam_f8160c220c72")

        assertTrue(exact.isExact)
        assertEquals("ActionCam_f8160c220c72", exact.name)
        assertEquals("secret-pass", exact.password)
    }

    @Test
    fun `its description never carries the password`() {
        assertEquals("CameraNetwork(ActionCam_*)", CameraNetwork().toString())
        assertEquals("CameraNetwork(Trail)", CameraNetwork(name = "Trail", password = "hunter2hunter2").toString())
        assertFalse("hunter2" in CameraNetwork(name = "Trail", password = "hunter2hunter2").toString())
    }

    @Test
    fun `a name, prefix or password the phone could not use is refused`() {
        assertFailsWith<IllegalArgumentException> { CameraNetwork(name = " ") }
        assertFailsWith<IllegalArgumentException> { CameraNetwork(prefix = "") }
        assertFailsWith<IllegalArgumentException> { CameraNetwork(password = "short") }
        assertFailsWith<IllegalArgumentException> { CameraNetwork(password = "x".repeat(64)) }
        assertFailsWith<IllegalArgumentException> { CameraNetwork(password = "tab\there!") }
        assertEquals(63, CameraNetwork(password = "x".repeat(63)).password.length)
    }

    @Test
    fun `a link failure carries why`() {
        val failure = CameraLinkException(LinkFailure.WifiOff, "Wi-Fi is off.")

        assertEquals(LinkFailure.WifiOff, failure.failure)
        assertEquals("Wi-Fi is off.", failure.message)
    }

    @Test
    fun `a password with a character outside plain ascii is refused`() {
        assertFailsWith<IllegalArgumentException> { CameraNetwork(password = "caf\u00e9-password") }
    }
}
