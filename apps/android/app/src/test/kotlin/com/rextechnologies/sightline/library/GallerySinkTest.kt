package com.rextechnologies.sightline.library

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.core.library.MediaKind
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import java.io.File
import java.io.IOException
import java.time.LocalDateTime
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/** The gallery, against a stand-in for Android's media provider that records what it was asked. */
@RunWith(AndroidJUnit4::class)
class GallerySinkTest {
    /** Android's media store, as far as saving a file needs it. */
    class FakeMediaStore : ContentProvider() {
        val inserted = mutableMapOf<Uri, ContentValues>()
        val updated = mutableMapOf<Uri, ContentValues>()
        val deleted = mutableListOf<Uri>()
        val files = mutableMapOf<Uri, File>()
        var refuseInsert = false
        var refuseOpen = false

        /** Opens nothing and says nothing, which a provider is allowed to do. */
        var opensNothing = false

        /** Hands out files that cannot be written, as a full or read-only volume does. */
        var readOnly = false

        override fun onCreate() = true

        override fun insert(uri: Uri, values: ContentValues?): Uri? {
            if (refuseInsert) return null
            val item = Uri.withAppendedPath(uri, (inserted.size + 1).toString())
            inserted[item] = ContentValues(values)
            files[item] = File.createTempFile("media", ".bin")
            return item
        }

        override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor? {
            if (refuseOpen) throw java.io.FileNotFoundException("The volume is not mounted.")
            if (opensNothing) return null
            val mode = if (readOnly) ParcelFileDescriptor.MODE_READ_ONLY else ParcelFileDescriptor.MODE_READ_WRITE
            return ParcelFileDescriptor.open(files.getValue(uri), mode)
        }

        override fun update(
            uri: Uri,
            values: ContentValues?,
            selection: String?,
            selectionArgs: Array<out String>?,
        ): Int {
            updated[uri] = ContentValues(values)
            return 1
        }

        override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int {
            deleted += uri
            return 1
        }

        override fun query(
            uri: Uri,
            projection: Array<out String>?,
            selection: String?,
            selectionArgs: Array<out String>?,
            sortOrder: String?,
        ): Cursor? = null

        override fun getType(uri: Uri): String? = null
    }

    private lateinit var store: FakeMediaStore
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val photo = CameraFile('J', 3, LocalDateTime.of(2024, 1, 31, 18, 35, 58), 1)

    @Before
    fun installStore() {
        store = Robolectric.setupContentProvider(FakeMediaStore::class.java, MediaStore.AUTHORITY)
    }

    @Test
    fun `a copy is written pending in a sightline folder and published once whole`() {
        val pending = GallerySink(context.contentResolver).create(photo, MediaKind.Jpeg)
        pending.output.write(byteArrayOf(1, 2, 3))
        pending.output.close()

        val where = pending.publish()

        val (uri, values) = store.inserted.entries.single()
        assertEquals("PICT0003.jpg", values.getAsString(MediaStore.MediaColumns.DISPLAY_NAME))
        assertEquals("image/jpeg", values.getAsString(MediaStore.MediaColumns.MIME_TYPE))
        assertEquals("Pictures/Sightline", values.getAsString(MediaStore.MediaColumns.RELATIVE_PATH))
        assertEquals(1, values.getAsInteger(MediaStore.MediaColumns.IS_PENDING))
        assertEquals(0, store.updated.getValue(uri).getAsInteger(MediaStore.MediaColumns.IS_PENDING))
        assertContentEquals(byteArrayOf(1, 2, 3), store.files.getValue(uri).readBytes())
        assertEquals("Pictures/Sightline/PICT0003.jpg", where)
    }

    @Test
    fun `a copy thrown away is deleted and never published`() {
        val pending = GallerySink(context.contentResolver).create(photo, MediaKind.Jpeg)

        pending.discard()

        assertEquals(listOf(store.inserted.keys.single()), store.deleted)
        assertTrue(store.updated.isEmpty())
    }

    @Test
    fun `photos, videos and anything else each go where a person would look`() {
        assertEquals(Environment.DIRECTORY_PICTURES, GallerySink.placeFor(MediaKind.Jpeg).second)
        assertEquals(Environment.DIRECTORY_MOVIES, GallerySink.placeFor(MediaKind.Avi).second)
        assertEquals(Environment.DIRECTORY_MOVIES, GallerySink.placeFor(MediaKind.Mp4).second)
        assertEquals(Environment.DIRECTORY_DOWNLOADS, GallerySink.placeFor(MediaKind.Unknown).second)
        assertEquals(
            MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY),
            GallerySink.placeFor(MediaKind.Mp4).first,
        )
    }

    @Test
    fun `a gallery that will not take the file is an io error and leaves nothing`() {
        store.refuseInsert = true

        assertFailsWith<IOException> { GallerySink(context.contentResolver).create(photo, MediaKind.Jpeg) }
    }

    @Test
    fun `a gallery that will not open the file deletes the entry it made`() {
        store.refuseOpen = true

        assertFailsWith<IOException> { GallerySink(context.contentResolver).create(photo, MediaKind.Jpeg) }

        assertEquals(listOf(store.inserted.keys.single()), store.deleted)
    }

    @Test
    fun `a gallery that gives nowhere to write is an io error and leaves nothing`() {
        store.opensNothing = true

        val failure =
            assertFailsWith<IOException> { GallerySink(context.contentResolver).create(photo, MediaKind.Jpeg) }

        assertTrue("gave nowhere to write" in failure.message.orEmpty())
        assertEquals(listOf(store.inserted.keys.single()), store.deleted)
    }

    @Test
    fun `a whole picture is saved in one go`() {
        val where = GallerySink(
            context.contentResolver,
            folder = "Sentry",
        ).saveWhole("SENTRY_1", MediaKind.Jpeg, byteArrayOf(9, 9))

        assertEquals("Pictures/Sentry/SENTRY_1.jpg", where)
        assertContentEquals(byteArrayOf(9, 9), store.files.values.single().readBytes())
    }

    @Test
    fun `a whole picture that cannot be written is thrown away`() {
        store.readOnly = true

        assertFailsWith<IOException> {
            GallerySink(context.contentResolver).saveWhole("SENTRY_1", MediaKind.Jpeg, byteArrayOf(9))
        }

        assertEquals(listOf(store.inserted.keys.single()), store.deleted)
        assertTrue(store.updated.isEmpty())
    }
}
