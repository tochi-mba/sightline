package com.rextechnologies.sightline.core.settings

import com.rextechnologies.sightline.core.sentry.Sensitivity
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SettingsTest {
    private class MapStore : SettingsStore {
        val saved = mutableMapOf<String, String>()

        override fun read(key: String): String? = saved[key]

        override fun write(key: String, value: String?) {
            if (value == null) saved.remove(key) else saved[key] = value
        }
    }

    private val store = MapStore()
    private val settings = Settings(store)

    @Test
    fun `every setting starts at its default`() {
        for (setting in AppSettings.all) {
            assertEquals(setting.default, settings[setting], setting.key)
        }
    }

    @Test
    fun `each kind of setting is saved and read back`() {
        settings[AppSettings.AutoConnect] = false
        settings[AppSettings.Grid] = GridOverlay.Thirds
        settings[AppSettings.SentryArmDelay] = 45
        settings[AppSettings.CameraPassword] = "a better password"

        assertFalse(settings[AppSettings.AutoConnect])
        assertEquals(GridOverlay.Thirds, settings[AppSettings.Grid])
        assertEquals(45, settings[AppSettings.SentryArmDelay])
        assertEquals("a better password", settings[AppSettings.CameraPassword])
        assertEquals(
            mapOf(
                "connection.auto_connect" to "false",
                "live.grid" to "Thirds",
                "sentry.arm_delay" to "45",
                "connection.camera_password" to "a better password",
            ),
            store.saved,
        )
    }

    @Test
    fun `text set to nothing is removed rather than saved empty`() {
        settings[AppSettings.CameraName] = "ActionCam_1"
        settings[AppSettings.CameraName] = null

        assertNull(settings[AppSettings.CameraName])
        assertTrue(store.saved.isEmpty())
    }

    @Test
    fun `a value a setting does not accept is refused and nothing is saved`() {
        assertFailsWith<IllegalArgumentException> { settings[AppSettings.SentryArmDelay] = 121 }
        assertFailsWith<IllegalArgumentException> { settings[AppSettings.SentryArmDelay] = 7 }
        assertFailsWith<IllegalArgumentException> { settings[AppSettings.CameraPassword] = "short" }
        val subset =
            Choice("test.subset", SettingGroup.Internal, "t", "s.", Sensitivity.Low, listOf(Sensitivity.Low)) {
                it.name
            }
        assertFailsWith<IllegalArgumentException> { settings[subset] = Sensitivity.High }

        assertTrue(store.saved.isEmpty())
    }

    @Test
    fun `a saved value that cannot be read back is its default, not an error`() {
        store.saved["connection.auto_connect"] = "maybe"
        store.saved["live.grid"] = "Purple"
        store.saved["sentry.arm_delay"] = "999"
        store.saved["sentry.cooldown"] = "ten"
        store.saved["connection.camera_password"] = "short"

        assertTrue(settings[AppSettings.AutoConnect])
        assertEquals(GridOverlay.None, settings[AppSettings.Grid])
        assertEquals(10, settings[AppSettings.SentryArmDelay])
        assertEquals(30, settings[AppSettings.SentryCooldown])
        assertEquals("12345678", settings[AppSettings.CameraPassword])
    }

    @Test
    fun `resetting puts the default back`() {
        settings[AppSettings.Flip] = true

        settings.reset(AppSettings.Flip)

        assertFalse(settings[AppSettings.Flip])
        assertTrue(store.saved.isEmpty())
        // Each change is counted, the reset included, for screens that redraw on any change.
        assertEquals(2L, settings.changes.value)
    }

    @Test
    fun `watching a setting gives its value now and each change, once each`() = runTest {
        val seen = mutableListOf<Boolean>()
        val watching = launch(UnconfinedTestDispatcher(testScheduler)) {
            settings.watch(AppSettings.Mirror).take(3).toList(seen)
        }

        settings[AppSettings.Mirror] = true
        settings[AppSettings.Flip] = true
        settings[AppSettings.Mirror] = true
        settings.reset(AppSettings.Mirror)
        watching.join()

        assertEquals(listOf(false, true, false), seen)
    }

    @Test
    fun `every setting is well formed, and every key is its own`() {
        assertEquals(AppSettings.all.size, AppSettings.all.map { it.key }.toSet().size)
        for (setting in AppSettings.all) {
            assertTrue(setting.title.isNotBlank(), setting.key)
            assertTrue(setting.summary.endsWith("."), setting.key)
            assertEquals(setting.key, setting.toString())
        }
    }

    @Test
    fun `every choice can be named on screen`() {
        for (setting in AppSettings.all.filterIsInstance<Choice<*>>()) {
            assertEquals(setting.options.size, setting.labels.size)
            assertTrue(setting.labels.all { it.isNotBlank() }, setting.key)
        }
        assertEquals(listOf("None", "Rule of thirds", "Centre mark"), AppSettings.Grid.labels)
    }

    @Test
    fun `the screen shows every setting but the app's own, in sections`() {
        assertFalse(SettingGroup.Internal in AppSettings.shown)
        assertEquals(
            AppSettings.all.count {
                it.group != SettingGroup.Internal
            },
            AppSettings.shown.values.sumOf { it.size },
        )
        assertEquals(SettingGroup.entries.dropLast(1), AppSettings.shown.keys.toList())
        assertEquals("Ride HUD", SettingGroup.Hud.title)
    }

    @Test
    fun `a setting that could never hold its own default is refused when it is made`() {
        assertFailsWith<IllegalArgumentException> {
            Choice("bad", SettingGroup.Internal, "t", "s.", Sensitivity.High, listOf(Sensitivity.Low)) { it.name }
        }
        assertFailsWith<IllegalArgumentException> { Amount("bad", SettingGroup.Internal, "t", "s.", 11, 0..10, 1, "s") }
        assertFailsWith<IllegalArgumentException> { Amount("bad", SettingGroup.Internal, "t", "s.", 0, 0..10, 3, "s") }
        assertFailsWith<IllegalArgumentException> { Amount("bad", SettingGroup.Internal, "t", "s.", 0, 0..10, 0, "s") }
    }

    @Test
    fun `the screen lists the settings in this order`() {
        assertEquals(
            listOf(
                AppSettings.AutoConnect,
                AppSettings.Reconnect,
                AppSettings.CameraPassword,
                AppSettings.CameraName,
                AppSettings.Fit,
                AppSettings.Grid,
                AppSettings.Flip,
                AppSettings.Mirror,
                AppSettings.KeepScreenOn,
                AppSettings.ShowStreamStats,
                AppSettings.Units,
                AppSettings.Layout,
                AppSettings.HudMirror,
                AppSettings.SentrySensitivity,
                AppSettings.SentryArmDelay,
                AppSettings.SentryCooldown,
                AppSettings.SentryRecords,
                AppSettings.SentrySnapshots,
                AppSettings.DeleteAfterCopy,
                AppSettings.CheckForUpdates,
                AppSettings.OnboardingDone,
                AppSettings.LastVersion,
            ),
            AppSettings.all,
        )
    }

    @Test
    fun `an amount says its range, its step and its unit, for the slider that sets it`() {
        assertEquals(0..120, AppSettings.SentryArmDelay.range)
        assertEquals(5, AppSettings.SentryArmDelay.step)
        assertEquals("seconds", AppSettings.SentryArmDelay.unit)
        assertFalse(AppSettings.SentryArmDelay.accepts(-5))
        assertFailsWith<IllegalArgumentException> { Amount("bad", SettingGroup.Internal, "t", "s.", -1, 0..10, 1, "s") }
    }

    @Test
    fun `a camera password is eight to sixty-three plain characters`() {
        assertTrue(AppSettings.CameraPassword.accepts("x".repeat(63)))
        assertFalse(AppSettings.CameraPassword.accepts("x".repeat(64)))
        assertFalse(AppSettings.CameraPassword.accepts("tab\tthere!"))
        assertFalse(AppSettings.CameraPassword.accepts("caf\u00e9-password"))
    }

    @Test
    fun `a blank camera name or version reads as none`() {
        store.saved["connection.camera_name"] = "   "
        store.saved["app.last_version"] = " "

        assertNull(settings[AppSettings.CameraName])
        assertNull(settings[AppSettings.LastVersion])
        settings[AppSettings.LastVersion] = "0.1.0"
        assertEquals("0.1.0", settings[AppSettings.LastVersion])
    }
}
