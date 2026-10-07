package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runTest
import org.junit.Rule
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.time.LocalDateTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Clips played from the card, kept on the phone so playing one again needs no download. */
class ClipCacheTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val taken = LocalDateTime.of(2026, 10, 7, 9, 30, 0)
    private val clip = FakeClip.reference(
        listOf(FakeRtspCamera.jpeg(500), FakeRtspCamera.jpeg(500)),
        listOf(ByteArray(16_000)),
    ).build()

    private fun video(index: Int, kilobytes: Long = 1) = CameraFile('A', index, taken, kilobytes)

    private fun TestScope.cache(limit: Long = ClipCache.DEFAULT_LIMIT) =
        ClipCache(File(temp.root, "clips"), limit, StandardTestDispatcher(testScheduler))

    private fun ClipCache.keep(file: CameraFile, bytes: ByteArray): File = start(file).use { pending ->
        pending.output.write(bytes)
        pending.keep()
    }

    /** A directory under [name] with something in it: a file in use, as far as deleting it goes, on any system. */
    private fun ClipCache.busy(name: String): File = File(folder, name).also {
        it.mkdirs()
        File(it, "inside").writeBytes(byteArrayOf(1))
    }

    @Test
    fun `a clip grows where a player can follow it and is kept whole under its real name`() = runTest {
        val cache = cache()
        val file = video(4)

        cache.start(file).use { pending ->
            pending.output.write(clip, 0, 100)
            assertTrue(pending.path.name.startsWith(".MOVI0004-"))
            RandomAccessFile(pending.path, "r").use { follower ->
                assertEquals(100L, follower.length())
                pending.output.write(clip, 100, clip.size - 100)
                assertEquals(clip.size.toLong(), follower.length())
            }

            assertEquals(cache.pathFor(file), pending.keep())
        }

        assertEquals(listOf(cache.pathFor(file)), cache.folder.listFiles()!!.toList())
        assertEquals("MOVI0004-20261007-093000-1k.avi", cache.pathFor(file).name)
        val kept = cache.open(file, this)!!
        assertEquals(ClipArrival.Kept(cache.pathFor(file)), kept.arrival.await())
        assertTrue(kept.reader.isComplete)
        assertEquals(clip.size.toLong(), kept.reader.bytesRead)
    }

    @Test
    fun `nothing kept means nothing to open`() = runTest {
        val cache = cache()

        assertNull(cache.open(video(1), this))
        assertEquals(ClipCache.DEFAULT_LIMIT, cache.limit)
        assertEquals("MOVI0005-undated-2k.avi", cache.pathFor(CameraFile('A', 5, null, 2)).name)
        assertFailsWith<IllegalArgumentException> { ClipCache(temp.root, 0) }
    }

    @Test
    fun `the clips played longest ago go first until the new one fits`() = runTest {
        val cache = cache(limit = 3L * clip.size + 512)
        val oldest = cache.keep(video(1), clip)
        val playedAgain = cache.keep(video(2), clip)
        val newer = cache.keep(video(3), clip)
        oldest.setLastModified(1_000_000)
        playedAgain.setLastModified(2_000_000)
        newer.setLastModified(3_000_000)
        cache.open(video(2), this)!!.arrival.await()

        // A kilobyte more does not fit beside all three; beside two it does.
        cache.start(video(4)).use { pending ->
            assertFalse(oldest.exists())
            assertTrue(playedAgain.exists())
            assertTrue(newer.exists())
            assertTrue(pending.path.exists())
        }
    }

    @Test
    fun `a clip bigger than the limit is still kept alone`() = runTest {
        val cache = cache(limit = 1024)
        val kept = cache.keep(video(1), clip)

        cache.start(video(2, kilobytes = 5)).use { pending ->
            assertFalse(kept.exists())
            assertTrue(pending.path.exists())
        }
    }

    @Test
    fun `pieces of fetches that never finished go and a file that will not go is left for another time`() = runTest {
        val cache = cache(limit = 1024)
        cache.keep(video(1), clip)
        val stale = File(cache.folder, ".MOVI0002-stale.part").also { it.writeBytes(byteArrayOf(1, 2, 3)) }
        val busyPiece = cache.busy(".MOVI0003-busy.part")
        // Played longest ago, so the first the cache tries to let go of.
        val busyClip = cache.busy("MOVI0009-busy.avi").also { it.setLastModified(1_000_000) }

        cache.start(video(4)).use {
            assertFalse(stale.exists())
            assertTrue(busyPiece.exists())
            assertTrue(busyClip.exists())
        }
    }

    @Test
    fun `a discarded piece is gone and a folder that cannot be made says so`() = runTest {
        val cache = cache()
        val thrown = cache.start(video(1))
        thrown.output.write(byteArrayOf(1, 2, 3))
        thrown.discard()
        assertFalse(thrown.path.exists())

        val blocked = File(temp.root, "not-a-folder").also { it.writeBytes(byteArrayOf(1)) }
        assertFailsWith<IOException> { ClipCache(blocked).start(video(2)) }
    }

    @Test
    fun `a damaged kept clip is thrown away so the next play fetches it again`() = runTest {
        val cache = cache()
        val damaged = cache.keep(video(1), byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12))

        val clip = cache.open(video(1), this)!!

        assertEquals(
            ClipArrival.Failed(
                "The copy kept on this phone was damaged, so it was thrown away. Play the clip again to fetch it from the card.",
            ),
            clip.arrival.await(),
        )
        assertFalse(damaged.exists())
        assertNull(cache.open(video(1), this))
    }

    @Test
    fun `a kept clip the phone cannot read says so and stays`() = runTest {
        val cache = cache()
        val kept = cache.pathFor(video(1)).also { it.mkdirs() }

        val clip = cache.open(video(1), this)!!

        val failed = assertIs<ClipArrival.Failed>(clip.arrival.await())
        assertTrue(failed.reason.startsWith("The copy kept on this phone could not be read: "), failed.reason)
        assertTrue(kept.exists())
    }

    @Test
    fun `a kept clip let go before it is read back is stopped`() = runTest {
        val cache = cache()
        cache.keep(video(1), clip)
        val kept = cache.open(video(1), this)!!

        kept.close()

        assertEquals(ClipArrival.Stopped, kept.arrival.await())
        assertFalse(kept.reader.isComplete)
    }
}
