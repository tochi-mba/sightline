package com.rextechnologies.sightline.core.updates

/**
 * A release newer than the one running, as GitHub lists it.
 *
 * @property version Its version, without the leading v of its tag.
 * @property page Where a person downloads it.
 */
data class Release(val version: String, val page: String)

/**
 * Reads GitHub's answer about the latest release and says whether it is newer than this build.
 *
 * Only tagged, stable releases count: GitHub's "latest" leaves prereleases out, which is the same rule the
 * site follows when it offers a download. The answer is read for two fields only, by pattern, so the core
 * needs no JSON library; anything it cannot read is no update rather than an error, because an update
 * check must never be what stops the app working.
 */
object UpdateCheck {
    /** Where GitHub answers with the latest release, over the phone's ordinary internet connection. */
    const val LATEST = "https://api.github.com/repos/tochi-mba/sightline/releases/latest"

    /** The release in [answer], when it is newer than [current]; otherwise null. */
    fun newer(answer: String, current: String): Release? {
        val tag = field(answer, "tag_name") ?: return null
        val page = field(answer, "html_url") ?: return null
        val version = tag.removePrefix("v")
        val offered = VersionNumbers.parse(version) ?: return null
        val running = VersionNumbers.parse(current) ?: return null
        return if (offered > running) Release(version, page) else null
    }

    private fun field(answer: String, name: String): String? =
        Regex(""""$name"\s*:\s*"([^"\\]*)"""").find(answer)?.groupValues?.get(1)
}
