package com.rextechnologies.sightline.ui

/** The phone, as the screens see it: every permission granted unless told otherwise, every request recorded. */
class FakePlatform : Platform {
    var grants = true
    val asked = mutableListOf<String>()
    var screenOn = false
    var servicesStarted = 0

    override fun withNearbyPermission(then: () -> Unit) = ask("nearby", then)

    override fun withLocationPermission(then: () -> Unit) = ask("location", then)

    override fun withNotificationPermission(then: () -> Unit) {
        asked += "notifications"
        then()
    }

    override fun openWifiSettings() {
        asked += "wifi settings"
    }

    override fun openAppSettings() {
        asked += "app settings"
    }

    override fun openLink(url: String) {
        asked += url
    }

    override fun keepScreenOn(on: Boolean) {
        screenOn = on
    }

    override fun startCameraService() {
        servicesStarted++
    }

    private fun ask(what: String, then: () -> Unit) {
        asked += what
        if (grants) then()
    }
}
