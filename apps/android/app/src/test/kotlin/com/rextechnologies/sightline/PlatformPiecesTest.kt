package com.rextechnologies.sightline

import android.app.NotificationManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.live.FrameDecoder
import com.rextechnologies.sightline.sentry.LumaSampler
import com.rextechnologies.sightline.settings.PreferencesStore
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import java.io.ByteArrayOutputStream
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNotSame
import kotlin.test.assertNull
import kotlin.test.assertSame

/** The small pieces of the app that need Android: preferences, the version, notifications, decoding. */
@RunWith(AndroidJUnit4::class)
class PlatformPiecesTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()

    /** A real JPEG of one colour, [width] by [height]. */
    private fun jpeg(width: Int, height: Int, colour: Int = Color.rgb(200, 100, 50)): ByteArray {
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888).apply { eraseColor(colour) }
        return ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.JPEG, 95, it) }.toByteArray()
    }

    @Test
    fun `settings are kept in the phone's preferences, and a value of the wrong type reads as unset`() {
        val preferences = context.getSharedPreferences("test", Context.MODE_PRIVATE)
        val store = PreferencesStore(preferences)

        store.write("a", "one")
        assertEquals("one", store.read("a"))
        store.write("a", null)
        assertNull(store.read("a"))
        preferences.edit().putInt("number", 4).commit()
        assertNull(store.read("number"))
        PreferencesStore(context).write("b", "two")
        assertEquals("two", PreferencesStore(context).read("b"))
    }

    @Test
    fun `the version is the installed package's, or a placeholder when it has none`() {
        val info = shadowOf(context.packageManager).getInternalMutablePackageInfo(context.packageName)
        info.versionName = "1.2.3"
        assertEquals("1.2.3", BuildInfo.versionName(context))

        info.versionName = null
        assertEquals(BuildInfo.UNKNOWN, BuildInfo.versionName(context))
    }

    @Test
    fun `the application makes its graph once, and a test can install its own`() {
        val application = ApplicationProvider.getApplicationContext<SightlineApplication>()
        val made = application.graph
        assertSame(made, application.graph)

        val installed = TestGraph().graph
        application.graph = installed
        assertSame(installed, application.graph)
    }

    @Test
    fun `both notification channels are made, the alarms loud and the camera quiet`() {
        Notifications.ensureChannels(context)
        Notifications.ensureChannels(context)

        val manager = context.getSystemService(NotificationManager::class.java)
        assertEquals(
            NotificationManager.IMPORTANCE_LOW,
            manager.getNotificationChannel(Notifications.CAMERA_CHANNEL).importance,
        )
        assertEquals(
            NotificationManager.IMPORTANCE_HIGH,
            manager.getNotificationChannel(Notifications.SENTRY_CHANNEL).importance,
        )
        assertEquals(
            MainActivity::class.java.name,
            shadowOf(Notifications.openApp(context)).savedIntent.component?.className,
        )
    }

    @Test
    fun `frames are decoded into reused bitmaps, a new size gets a new one, and rubbish gets none`() {
        val decoder = FrameDecoder()
        val frame = jpeg(64, 36)

        val first = assertNotNull(decoder.decode(frame))
        val second = assertNotNull(decoder.decode(frame))
        val third = assertNotNull(decoder.decode(frame))
        val fourth = assertNotNull(decoder.decode(frame))

        assertEquals(64, first.width)
        assertNotSame(first, second)
        assertNotSame(second, third)
        assertSame(first, fourth)
        // Larger than the bitmap waiting to be reused: decoded into a new one instead.
        assertEquals(128, assertNotNull(decoder.decode(jpeg(128, 72))).width)
        assertNull(decoder.decode(byteArrayOf(1, 2, 3)))
    }

    @Test
    fun `sentry sees a frame as a small grid of brightness`() {
        val grid = assertNotNull(LumaSampler.sample(jpeg(640, 360, Color.WHITE)))

        assertEquals(40, grid.width)
        // A sixteenth of 360 is 22.5, and the decoder rounds down.
        assertEquals(22, grid.height)
        assertEquals(255, grid.cells[0], 2)
        assertNull(LumaSampler.sample(byteArrayOf(1, 2, 3)))
        assertEquals(0, LumaSampler.luma(Color.BLACK))
        assertEquals(76, LumaSampler.luma(Color.RED))
        assertEquals(149, LumaSampler.luma(Color.GREEN))
        assertEquals(29, LumaSampler.luma(Color.BLUE))
    }

    private fun assertEquals(expected: Int, actual: Int, tolerance: Int) {
        kotlin.test.assertTrue(
            kotlin.math.abs(expected - actual) <= tolerance,
            "$actual is not within $tolerance of $expected",
        )
    }
}
