package com.rextechnologies.sightline.sentry

import android.app.Notification
import android.app.NotificationManager
import android.content.Context
import android.graphics.Bitmap
import android.provider.MediaStore
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.Notifications
import com.rextechnologies.sightline.library.GallerySink
import com.rextechnologies.sightline.library.GallerySinkTest
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.Shadows.shadowOf
import java.io.ByteArrayOutputStream
import java.time.Instant
import java.time.ZoneOffset
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class AndroidSentryActionsTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val manager = context.getSystemService(NotificationManager::class.java)
    private lateinit var store: GallerySinkTest.FakeMediaStore
    private val at = Instant.parse("2026-10-05T21:42:07Z")
    private val actions by lazy { AndroidSentryActions(context, GallerySink(context.contentResolver), ZoneOffset.UTC) }

    private val snapshot: ByteArray = ByteArrayOutputStream().also {
        Bitmap.createBitmap(64, 36, Bitmap.Config.ARGB_8888).compress(Bitmap.CompressFormat.JPEG, 90, it)
    }.toByteArray()

    @Before
    fun setUp() {
        Notifications.ensureChannels(context)
        store = Robolectric.setupContentProvider(GallerySinkTest.FakeMediaStore::class.java, MediaStore.AUTHORITY)
    }

    private fun posted(): Notification = shadowOf(manager).getNotification(Notifications.SENTRY_NOTIFICATION)

    @Test
    fun `an alarm notifies with the picture that raised it, and saves it`() {
        actions.alarmRaised(at, snapshot, saveSnapshot = true)

        val notification = posted()
        assertEquals("Motion seen at 21:42:07", notification.extras.getString(Notification.EXTRA_TITLE))
        assertEquals("The picture is in your gallery.", notification.extras.getString(Notification.EXTRA_TEXT))
        assertNotNull(notification.getLargeIcon())
        assertEquals(
            "SENTRY_20261005-214207.jpg",
            store.inserted.values.single().getAsString(MediaStore.MediaColumns.DISPLAY_NAME),
        )
    }

    @Test
    fun `with snapshots off, or a gallery that refuses, it only notifies`() {
        actions.alarmRaised(at, snapshot, saveSnapshot = false)
        assertEquals("Open Sightline to see the camera.", posted().extras.getString(Notification.EXTRA_TEXT))
        assertTrue(store.inserted.isEmpty())

        store.refuseInsert = true
        actions.alarmRaised(at, snapshot, saveSnapshot = true)
        assertEquals("Open Sightline to see the camera.", posted().extras.getString(Notification.EXTRA_TEXT))
    }

    @Test
    fun `a picture that does not decode is left out, and the alarm ending leaves the notification`() {
        actions.alarmRaised(at, byteArrayOf(1, 2, 3), saveSnapshot = false)
        actions.alarmEnded()

        assertNull(posted().getLargeIcon())
        assertEquals(1, shadowOf(manager).allNotifications.size)
        AndroidSentryActions(context).alarmEnded()
    }
}
