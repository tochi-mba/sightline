package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.runTest
import java.io.IOException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.time.Duration.Companion.seconds

/** The one feed a session shares, on its own: what it does at the edges a session reaches only by racing. */
class LiveFeedTest {
    @Test
    fun `a feed that is over takes no new watcher and tells the ones it had why`() = runTest {
        val retired = CompletableDeferred<LiveFeed>()
        val feed = LiveFeed(
            backgroundScope,
            { throw RtspException("The camera refused the video track (454).") },
            1.seconds,
            { null },
            testScheduler.timeSource,
        ) { retired.complete(it) }

        assertSame(feed, retired.await())
        assertNull(feed.watch())
        val refused = assertFailsWith<RtspException> { feed.first.pictures.receive() }
        assertEquals("The camera refused the video track (454).", refused.message)

        feed.first.leave()
        feed.stop()
        feed.stop()
    }

    @Test
    fun `a start that fails without saying why is still told`() = runTest {
        val feed = LiveFeed(backgroundScope, { throw IOException() }, 1.seconds, { null }, testScheduler.timeSource) {}

        val failed = assertFailsWith<RtspException> { feed.first.pictures.receive() }

        assertEquals("The stream could not be started.", failed.message)
    }
}
