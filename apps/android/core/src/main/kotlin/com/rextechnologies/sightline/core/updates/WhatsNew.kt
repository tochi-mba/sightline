package com.rextechnologies.sightline.core.updates

/**
 * What one version brought, in words a person reads once.
 *
 * @property version The version that introduced it, as bare numbers: `0.2.0`.
 * @property lines A few short lines, each one thing somebody can now do.
 */
data class WhatsNewEntry(val version: String, val lines: List<String>)

/**
 * A version's three numbers, compared as numbers: 0.10.0 is after 0.9.0.
 */
data class VersionNumbers(val major: Int, val minor: Int, val patch: Int) : Comparable<VersionNumbers> {
    override fun compareTo(
        other: VersionNumbers,
    ): Int = compareValuesBy(this, other, { it.major }, { it.minor }, { it.patch })

    companion object {
        /**
         * A version's numbers, with anything after them ignored, or null when there are none.
         *
         * `0.2.0-rolling.12+g1abc` reads as 0.2.0: the suffix orders builds, not features. `0.2` alone is
         * not a version this app ever writes, so it is unreadable rather than read leniently.
         */
        fun parse(version: String?): VersionNumbers? {
            val bare = version?.trim()?.substringBefore('-')?.substringBefore('+') ?: return null
            val parts = bare.split('.')
            if (parts.size != 3) {
                return null
            }

            // Digits only, so "+1" and " 1" are not read as numbers; too many digits does not fit and is
            // unreadable too.
            val numbers = parts.map { part ->
                part.takeIf { it.isNotEmpty() && it.all(Char::isDigit) }?.toIntOrNull() ?: return null
            }
            return VersionNumbers(numbers[0], numbers[1], numbers[2])
        }
    }
}

/**
 * Decides whether an update is worth a word, and which words.
 *
 * After the app updates itself, the next launch may greet the person with what changed, but only when
 * something changed for them. The notes here are curated by hand, a few lines per version that earned
 * them; most updates are fixes and ship silently. A first install says nothing either: everything is new,
 * and onboarding is the introduction. Rolling builds of one version are the same version to a person, so
 * moving between them never greets, and a downgrade claims nothing, because nothing is new.
 *
 * The same rules as the Windows app's catalogue, so the two never disagree about what is worth saying.
 */
object WhatsNewCatalog {
    /** The curated notes, newest first. A version with nothing worth a person's time has no entry. */
    val entries: List<WhatsNewEntry> = listOf(
        // 0.1.0 is the first version; a first install greets nobody, so it needs no entry yet.
        // When 0.2.0 ships, its entry goes here, above this comment.
    )

    /**
     * The notes to show when the app that last ran was [previous] and this one is [current]; empty when
     * nothing needs saying.
     *
     * @param previous The version recorded at the last run, or null on a first run.
     * @param current This build's version.
     * @param entries The notes to choose from; the curated ones when omitted.
     */
    fun since(previous: String?, current: String, entries: List<WhatsNewEntry> = this.entries): List<WhatsNewEntry> {
        val from = VersionNumbers.parse(previous)
        val to = VersionNumbers.parse(current)
        if (from == null || to == null || from >= to) {
            // A first run, an unreadable record, the same version, or a downgrade: say nothing. An
            // unreadable record reads as a first run because greeting somebody with notes for every
            // version ever shipped would be worse than greeting them with none.
            return emptyList()
        }

        return entries
            .mapNotNull { entry -> VersionNumbers.parse(entry.version)?.let { it to entry } }
            .filter { (at, _) -> at > from && at <= to }
            .sortedByDescending { (at, _) -> at }
            .map { (_, entry) -> entry }
    }
}
