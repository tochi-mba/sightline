package com.rextechnologies.sightline.core.navigation

/**
 * The app's four top-level places, in the order its navigation lists them.
 *
 * HUD is a mode of [Live] rather than a place of its own. Everything else, a camera setting or a
 * clip from the card, opens on top of one of these four.
 */
enum class Destination {
    /** The camera's picture and its controls. Where the app opens. */
    Live,

    /** What is on the camera's card. */
    Library,

    /** The camera as a security camera, watching for motion. */
    Sentry,

    /** The app's own settings and the camera's. */
    Settings,
    ;

    companion object {
        /** Where the app opens, and the last place back reaches before it leaves the app. */
        val Start: Destination = Live
    }
}
