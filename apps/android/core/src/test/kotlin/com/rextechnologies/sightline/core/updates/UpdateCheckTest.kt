package com.rextechnologies.sightline.core.updates

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class UpdateCheckTest {
    /** The shape of GitHub's answer, cut down: the release's page comes before its author's. */
    private fun answer(tag: String) = """
        {"url":"https://api.github.com/repos/tochi-mba/sightline/releases/1",
         "html_url":"https://github.com/tochi-mba/sightline/releases/tag/$tag",
         "author":{"login":"tochi-mba","html_url":"https://github.com/tochi-mba"},
         "tag_name" : "$tag","prerelease":false}
    """.trimIndent()

    @Test
    fun `a newer tagged release is offered with its page`() {
        assertEquals(
            Release("0.2.0", "https://github.com/tochi-mba/sightline/releases/tag/v0.2.0"),
            UpdateCheck.newer(answer("v0.2.0"), "0.1.0"),
        )
        assertEquals("0.10.0", UpdateCheck.newer(answer("0.10.0"), "0.9.3-rolling.4")?.version)
    }

    @Test
    fun `the same version, an older one, or a rolling build of this one is no update`() {
        assertNull(UpdateCheck.newer(answer("v0.1.0"), "0.1.0"))
        assertNull(UpdateCheck.newer(answer("v0.0.9"), "0.1.0"))
        assertNull(UpdateCheck.newer(answer("v0.1.0"), "0.1.0-rolling.7"))
    }

    @Test
    fun `an answer that cannot be read is no update rather than an error`() {
        assertNull(UpdateCheck.newer("""{"message":"API rate limit exceeded"}""", "0.1.0"))
        assertNull(UpdateCheck.newer("""{"tag_name":"v0.2.0"}""", "0.1.0"))
        assertNull(UpdateCheck.newer(answer("nightly"), "0.1.0"))
        assertNull(UpdateCheck.newer(answer("v0.2.0"), "unknown"))
        assertNull(UpdateCheck.newer("<html>", "0.1.0"))
    }
}
