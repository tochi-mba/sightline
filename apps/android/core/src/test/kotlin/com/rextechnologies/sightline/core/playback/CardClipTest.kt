package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** A clip from the card as a player holds it: its bytes, how its arrival ended, and letting it go. */
class CardClipTest {
    private val video = CameraFile('A', 1, null, 1)

    @Test
    fun `before its bytes arrive there is nothing to open`() {
        val clip = CardClip(video)

        assertEquals(video, clip.file)
        assertFalse(clip.hasBytes)
        assertEquals(
            "The clip has not started arriving.",
            assertFailsWith<IllegalStateException> {
                clip.openRead()
            }.message,
        )
        assertFalse(clip.arrival.isCompleted)
    }

    @Test
    fun `how its arrival ended is told once and the first word stands`() = runTest {
        val clip = CardClip(video)

        clip.kept(File("kept.avi"))
        clip.failed("too late")
        clip.stopped()

        val kept = clip.arrival.await()
        assertEquals(ClipArrival.Kept(File("kept.avi")), kept)
        assertEquals(File("kept.avi"), (kept as ClipArrival.Kept).where)
        assertTrue(clip.hasBytes)
    }

    @Test
    fun `letting it go stops its work whether that work has begun or not`() = runTest {
        val running = CardClip(video)
        val work = launch(start = CoroutineStart.LAZY) { }
        running.runs(work)
        running.close()
        assertTrue(work.isCancelled)

        val late = CardClip(video)
        late.close()
        assertTrue(late.isStopped)
        val notYet = Job()
        late.runs(notYet)
        assertTrue(notYet.isCancelled)
    }
}
