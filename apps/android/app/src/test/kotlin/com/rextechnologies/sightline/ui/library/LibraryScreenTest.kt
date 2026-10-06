package com.rextechnologies.sightline.ui.library

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.design.RexTheme
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraFileKind
import com.rextechnologies.sightline.protocol.gpsock.NakCode
import com.rextechnologies.sightline.ui.FakePlatform
import com.rextechnologies.sightline.ui.SightlineApp
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Duration
import java.time.LocalDateTime
import kotlin.test.assertEquals
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class LibraryScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val test = TestGraph().also { it.graph.settings[AppSettings.OnboardingDone] = true }
    private val taken = LocalDateTime.of(2026, 10, 4, 18, 35, 56)

    /**
     * Opens the card page, then connects when [connect] says to: with nobody watching the picture then,
     * the card is read as soon as the camera is there, at no cost.
     */
    private fun openCard(connect: Boolean = false) {
        compose.setContent { RexTheme { SightlineApp(test.graph, FakePlatform()) } }
        compose.onNodeWithText("CARD").performClick()
        if (connect) {
            test.connected()
        }

        settle()
    }

    private fun settle() {
        test.settle(Duration.ofSeconds(1))
        compose.waitForIdle()
    }

    @Test
    fun `once the picture has been seen the card waits to be asked and says what it costs`() {
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        compose.setContent { RexTheme { SightlineApp(test.graph, FakePlatform()) } }
        // Drawn first, so the Live page is there to offer the picture when the camera connects.
        compose.waitForIdle()
        test.connected()
        compose.waitForIdle()
        compose.onNodeWithText("SHOW THE LIVE PICTURE").performClick()
        test.until { it.holdsLivePicture }

        compose.onNodeWithText("CARD").performClick()
        settle()

        compose.onNodeWithText(
            "Reading the card ends the live picture, which then needs the camera's battery taken out and " +
                "put back: it cannot show its picture and list its files at once.",
        ).assertExists()
        assertTrue(test.graph.controller.state.value.library.files == null)
        compose.onNodeWithText("READ THE CARD").performClick()
        test.until { it.library.files != null && it.task == null }
        compose.waitForIdle()
        compose.onNodeWithText("1 photo").assertExists()
    }

    @Test
    fun `with no camera it says to connect one`() {
        openCard()

        compose.onNodeWithText("No camera connected").assertExists()
    }

    @Test
    fun `opening the card lists its files by day, and copying puts them on the phone`() {
        // Real thumbnails, so each file's picture is drawn rather than its kind.
        test.camera.thumbnailOf = { TestGraph.picture() }
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        test.camera.addFile('A', taken.plusDays(1), ByteArray(5000))
        openCard(connect = true)

        compose.onNodeWithText("1 video, 1 photo").assertExists()
        compose.onNodeWithText("SUNDAY 4 OCTOBER 2026").assertExists()
        compose.onNodeWithText("MONDAY 5 OCTOBER 2026").assertExists()
        compose.onNodeWithContentDescription("Photo, 4 October 2026, 18:35, 3 KB").performClick()
        compose.onNodeWithText("COPY 1 TO PHONE").performClick()
        settle()

        assertTrue(test.gallery.published.containsKey("Gallery/PICT0001.jpg"))
        compose.onNodeWithText("On your phone").assertExists()
    }

    @Test
    fun `selected files are deleted only after a confirmation`() {
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        openCard(connect = true)

        compose.onAllNodes(hasContentDescription("Photo, 4 October 2026, 18:35, 3 KB")).assertCountEquals(2)
        compose.onAllNodes(hasText("PHOTO"))[0].performClick()
        compose.onAllNodes(hasText("PHOTO"))[1].performClick()
        compose.onNodeWithText("DELETE").performClick()
        compose.onNodeWithText("Delete 2 files from the camera?").assertExists()
        compose.onNodeWithText("CANCEL").performClick()
        compose.onNodeWithText("DELETE").performClick()
        compose.onNode(hasText("DELETE") and hasAnyAncestor(isDialog())).performClick()
        settle()

        assertTrue(test.camera.files.isEmpty())
        compose.onNodeWithText("The card is empty").assertExists()
    }

    @Test
    fun `one file deleted is asked about in the singular, and selecting again deselects`() {
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        openCard(connect = true)

        compose.onNodeWithText("PHOTO").performClick()
        compose.onNodeWithText("PHOTO").performClick()
        compose.onNodeWithText("READ THE CARD AGAIN").assertExists()
        compose.onNodeWithText("PHOTO").performClick()
        compose.onNodeWithText("DELETE").performClick()

        compose.onNodeWithText("Delete this file from the camera?").assertExists()
    }

    @Test
    fun `an empty card says so, and the card can be read again`() {
        openCard(connect = true)

        compose.onNodeWithText("The card is empty").assertExists()
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        compose.onNodeWithText("READ THE CARD AGAIN").performClick()
        settle()

        compose.onNodeWithText("1 photo").assertExists()
    }

    @Test
    fun `copies waiting and in progress each say so`() {
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        test.camera.downloadStallsAfterBytes = 1000
        test.camera.downloadChunk = 500
        openCard(connect = true)

        compose.onAllNodes(hasText("PHOTO"))[0].performClick()
        compose.onAllNodes(hasText("PHOTO"))[1].performClick()
        compose.onNodeWithText("COPY 2 TO PHONE").performClick()
        test.settle()
        compose.waitForIdle()

        compose.onNodeWithContentDescription("Copying").assertExists()
        compose.onNodeWithText("Waiting to copy").assertExists()
    }

    @Test
    fun `a copy the camera refuses says why`() {
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        test.camera.refuseDownloadWith = NakCode.FullStorage
        openCard(connect = true)

        compose.onNodeWithText("PHOTO").performClick()
        compose.onNodeWithText("COPY 1 TO PHONE").performClick()
        settle()

        compose.onNodeWithText("The camera said no: the card is full.").assertExists()
    }

    @Test
    fun `the words for files read as people say them`() {
        assertEquals("295 KB", sizeWords(295 * 1024))
        assertEquals("9.1 MB", sizeWords(9_500_000))
        assertEquals("2.3 GB", sizeWords(2_500_000_000))
        assertEquals("Locked video", kindWord(CameraFileKind.ProtectedVideo))
        assertEquals("Emergency video", kindWord(CameraFileKind.EmergencyVideo))
        assertEquals("File", kindWord(CameraFileKind.Other))
        assertEquals("Nothing on the card", countWords(emptyList()))
        val other = CameraFile('X', 1, null, 1)
        assertEquals("1 other file", countWords(listOf(other)))
        assertEquals("File, 1 KB", describe(other))
        assertEquals(listOf("Date unknown"), byDay(listOf(other)).map { it.first })
        assertEquals("2 photos", countWords(listOf(CameraFile('J', 1, null, 1), CameraFile('J', 2, null, 1))))
    }

    @Test
    fun `with the setting on, a file safely copied is deleted from the card`() {
        test.graph.settings[AppSettings.DeleteAfterCopy] = true
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        openCard(connect = true)

        compose.onNodeWithText("PHOTO").performClick()
        compose.onNodeWithText("COPY 1 TO PHONE").performClick()
        settle()

        assertTrue(test.camera.files.isEmpty())
        assertTrue(test.gallery.published.containsKey("Gallery/PICT0001.jpg"))
    }
}
