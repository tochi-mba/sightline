package com.rextechnologies.sightline.core.updates

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The same cases the Windows catalogue is held to, so the two apps greet an update alike. */
class WhatsNewTest {
    private val notes = listOf(
        WhatsNewEntry("0.2.0", listOf("Two.")),
        WhatsNewEntry("0.4.0", listOf("Four.")),
        WhatsNewEntry("0.3.0", listOf("Three.")),
    )

    @Test
    fun `values the app never writes have no numbers`() {
        val unreadable = listOf(
            null,
            "",
            "   ",
            "not-a-version",
            "1.2",
            "1.2.3.4",
            "1..3",
            "+1.2.3",
            "1.2.x",
            "1.2.99999999999",
        )
        for (value in unreadable) {
            assertNull(VersionNumbers.parse(value), value.toString())
        }
    }

    @Test
    fun `a version is its numbers, not its build labels`() {
        assertEquals(VersionNumbers(0, 2, 0), VersionNumbers.parse("0.2.0"))
        assertEquals(VersionNumbers(2, 14, 7), VersionNumbers.parse("2.14.7-rolling.12"))
        assertEquals(VersionNumbers(3, 1, 4), VersionNumbers.parse("3.1.4+g1abc"))
        assertEquals(VersionNumbers(5, 0, 0), VersionNumbers.parse("5.0.0-preview.2+sha"))
    }

    @Test
    fun `versions compare as numbers, part by part`() {
        assertTrue(VersionNumbers(0, 10, 0) > VersionNumbers(0, 9, 0))
        assertTrue(VersionNumbers(1, 0, 0) > VersionNumbers(0, 99, 99))
        assertTrue(VersionNumbers(0, 1, 2) > VersionNumbers(0, 1, 1))
        assertEquals(0, VersionNumbers(1, 2, 3).compareTo(VersionNumbers(1, 2, 3)))
    }

    @Test
    fun `a first install has no update notes`() {
        assertTrue(WhatsNewCatalog.since(null, "0.4.0", notes).isEmpty())
    }

    @Test
    fun `an unreadable record, the same version and a downgrade have no notes`() {
        assertTrue(WhatsNewCatalog.since("broken", "0.4.0", notes).isEmpty())
        assertTrue(WhatsNewCatalog.since("0.2.0", "broken", notes).isEmpty())
        assertTrue(WhatsNewCatalog.since("0.2.0", "0.2.0-rolling.17", notes).isEmpty())
        assertTrue(WhatsNewCatalog.since("0.4.0", "0.3.0", notes).isEmpty())
    }

    @Test
    fun `only the versions crossed are shown, newest first, and bad entries are skipped`() {
        val result = WhatsNewCatalog.since("0.1.0", "0.3.5+sha", notes + WhatsNewEntry("bad", listOf("Never shown.")))

        assertEquals(listOf("0.3.0", "0.2.0"), result.map { it.version })
    }

    @Test
    fun `the curated catalogue starts deliberately empty`() {
        assertTrue(WhatsNewCatalog.entries.isEmpty())
        assertTrue(WhatsNewCatalog.since("0.0.1", "9.9.9").isEmpty())
    }

    @Test
    fun `an entry at or before the last version is not shown again, and its lines are kept as written`() {
        val result = WhatsNewCatalog.since("0.2.0", "0.4.0", notes)

        assertEquals(listOf("0.4.0", "0.3.0"), result.map { it.version })
        assertEquals(listOf("Four."), result.first().lines)
        val parsed = VersionNumbers.parse("1.22.333")!!
        assertEquals(Triple(1, 22, 333), Triple(parsed.major, parsed.minor, parsed.patch))
    }
}
