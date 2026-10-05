package com.rextechnologies.sightline.core.settings

import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.sentry.Sensitivity
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map

/**
 * Where the app's settings are written down: SharedPreferences on a phone, a map in tests.
 *
 * Text only, so the store needs no idea what a setting means and a setting's own type decides how it is
 * read back.
 */
interface SettingsStore {
    /** The text saved under [key], or null when nothing is. */
    fun read(key: String): String?

    /** Saves [value] under [key], or removes it when null. */
    fun write(key: String, value: String?)
}

/** The sections the settings screen shows, in order. */
enum class SettingGroup(val title: String) {
    Connection("Connection"),
    LiveView("Live view"),
    Hud("Ride HUD"),
    Sentry("Sentry"),
    Library("Library"),
    Updates("Updates"),

    /** Kept by the app for itself, never shown. */
    Internal("Internal"),
}

/**
 * One of the app's own settings.
 *
 * @property key Where it is saved. Never renamed: a renamed key loses what people chose.
 * @property group Which section shows it.
 * @property title Its name on the settings screen.
 * @property summary One sentence on what it does.
 * @property default Its value until somebody changes it, and after a saved value cannot be read.
 */
sealed class Setting<T>(
    val key: String,
    val group: SettingGroup,
    val title: String,
    val summary: String,
    val default: T,
) {
    /** [text] read back as a value, or null when it is not one this setting accepts. */
    abstract fun decode(text: String): T?

    /** [value] as text to save. */
    abstract fun encode(value: T): String

    /** Whether [value] is one this setting accepts. */
    open fun accepts(value: T): Boolean = true

    override fun toString(): String = key
}

/** On or off. */
class Toggle(key: String, group: SettingGroup, title: String, summary: String, default: Boolean) :
    Setting<Boolean>(key, group, title, summary, default) {
    override fun decode(text: String): Boolean? = text.toBooleanStrictOrNull()

    override fun encode(value: Boolean): String = value.toString()
}

/**
 * One of a fixed list.
 *
 * @property options What can be chosen, in the order the screen lists them.
 * @property label What each option is called on the screen.
 */
class Choice<E : Enum<E>>(
    key: String,
    group: SettingGroup,
    title: String,
    summary: String,
    default: E,
    val options: List<E>,
    private val label: (E) -> String,
) : Setting<E>(key, group, title, summary, default) {
    init {
        require(default in options) { "$key's default must be one of its options." }
    }

    /** What each option is called on the screen, in the order of [options]. */
    val labels: List<String> get() = options.map(label)

    override fun decode(text: String): E? = options.firstOrNull { it.name == text }

    override fun encode(value: E): String = value.name

    override fun accepts(value: E): Boolean = value in options
}

/**
 * A whole number in [range], counted in [unit].
 */
class Amount(
    key: String,
    group: SettingGroup,
    title: String,
    summary: String,
    default: Int,
    val range: IntRange,
    val step: Int,
    val unit: String,
) : Setting<Int>(key, group, title, summary, default) {
    init {
        require(default in range) { "$key's default must be in its range." }
        require(step > 0 && (range.last - range.first) % step == 0) { "$key's range must be whole steps of $step." }
    }

    override fun decode(text: String): Int? = text.toIntOrNull()?.takeIf(::accepts)

    override fun encode(value: Int): String = value.toString()

    override fun accepts(value: Int): Boolean = value in range && (value - range.first) % step == 0
}

/** Free text that [valid] accepts, or nothing: what the app records for itself, never shown. */
class Text(
    key: String,
    group: SettingGroup,
    title: String,
    summary: String,
    default: String?,
    private val valid: (String) -> Boolean,
) : Setting<String?>(key, group, title, summary, default) {
    override fun decode(text: String): String? = text.takeIf(valid)

    // Never given null: Settings removes the key instead of writing one.
    override fun encode(value: String?): String = value.toString()

    override fun accepts(value: String?): Boolean = value == null || valid(value)
}

/**
 * A Wi-Fi password. Never unset: its default is the one the camera ships with.
 *
 * @property rule What [valid] accepts, in words: what the person is told when what they typed is not it.
 */
class Password(
    key: String,
    group: SettingGroup,
    title: String,
    summary: String,
    default: String,
    val rule: String,
    private val valid: (String) -> Boolean,
) : Setting<String>(key, group, title, summary, default) {
    override fun decode(text: String): String? = text.takeIf(valid)

    override fun encode(value: String): String = value

    override fun accepts(value: String): Boolean = valid(value)
}

/** How the live picture is laid over the screen. */
enum class PictureFit { Fit, Fill }

/** Guides drawn over the live picture. */
enum class GridOverlay { None, Thirds, Centre }

/** How speed, height and distance are shown. */
enum class UnitSystem { Metric, Imperial }

/** How much the ride HUD shows. */
enum class HudLayout { Minimal, Ride, Cockpit }

/**
 * Every setting the app has, each with its default and what it accepts.
 *
 * The settings screen is drawn from this list, so a setting cannot exist without being shown, and none can
 * be shown without a default and a range.
 */
object AppSettings {
    val AutoConnect = Toggle(
        "connection.auto_connect",
        SettingGroup.Connection,
        "Connect when Sightline opens",
        "Joins the last camera you used as soon as the app opens.",
        true,
    )
    val Reconnect = Toggle(
        "connection.reconnect",
        SettingGroup.Connection,
        "Reconnect automatically",
        "Tries to get a lost camera back, five times, before saying it is gone.",
        true,
    )
    val CameraPassword = Password(
        "connection.camera_password",
        SettingGroup.Connection,
        "Camera Wi-Fi password",
        "What the camera's screen shows under WPA2. Most cameras keep the default.",
        CameraNetwork.DEFAULT_PASSWORD,
        "8 to 63 letters, numbers or symbols.",
    ) { it.length in 8..63 && it.all { character -> character in ' '..'~' } }
    val CameraName = Text(
        "connection.camera_name",
        SettingGroup.Internal,
        "Camera",
        "The Wi-Fi name of the camera last connected, so the next connect asks for exactly it.",
        null,
    ) { it.isNotBlank() }

    val Fit = Choice(
        "live.fit",
        SettingGroup.LiveView,
        "Picture",
        "Fit shows the whole picture; fill covers the screen and crops the edges.",
        PictureFit.Fit,
        PictureFit.entries,
    ) { it.name }
    val Grid = Choice(
        "live.grid",
        SettingGroup.LiveView,
        "Framing guide",
        "Lines drawn over the picture to help frame a shot.",
        GridOverlay.None,
        GridOverlay.entries,
    ) {
        when (it) {
            GridOverlay.None -> "None"
            GridOverlay.Thirds -> "Rule of thirds"
            GridOverlay.Centre -> "Centre mark"
        }
    }
    val Flip = Toggle(
        "live.flip",
        SettingGroup.LiveView,
        "Upside-down mount",
        "Turns the picture round for a camera mounted upside down.",
        false,
    )
    val Mirror = Toggle(
        "live.mirror",
        SettingGroup.LiveView,
        "Mirror",
        "Shows the picture mirrored, as a selfie camera does.",
        false,
    )
    val KeepScreenOn = Toggle(
        "live.keep_screen_on",
        SettingGroup.LiveView,
        "Keep the screen on",
        "Stops the phone sleeping while the live picture is showing.",
        true,
    )
    val ShowStreamStats = Toggle(
        "live.stats",
        SettingGroup.LiveView,
        "Show frame rate",
        "Shows how many pictures a second are arriving.",
        false,
    )

    val Units = Choice(
        "hud.units",
        SettingGroup.Hud,
        "Units",
        "Kilometres and metres, or miles and feet.",
        UnitSystem.Metric,
        UnitSystem.entries,
    ) { it.name }
    val Layout = Choice(
        "hud.layout",
        SettingGroup.Hud,
        "Layout",
        "Minimal shows speed; Ride adds heading and height; Cockpit shows everything.",
        HudLayout.Ride,
        HudLayout.entries,
    ) { it.name }
    val HudMirror = Toggle(
        "hud.mirror",
        SettingGroup.Hud,
        "Windscreen mode",
        "Mirrors the HUD so it reads the right way round reflected in a windscreen.",
        false,
    )

    val SentrySensitivity = Choice(
        "sentry.sensitivity",
        SettingGroup.Sentry,
        "Sensitivity",
        "How much has to move before Sentry raises the alarm.",
        Sensitivity.Medium,
        Sensitivity.entries,
    ) {
        when (it) {
            Sensitivity.Low -> "Low: people close by"
            Sensitivity.Medium -> "Medium: people anywhere"
            Sensitivity.High -> "High: anything that moves"
        }
    }
    val SentryArmDelay = Amount(
        "sentry.arm_delay",
        SettingGroup.Sentry,
        "Arming delay",
        "Time to leave before Sentry starts watching.",
        10,
        0..120,
        5,
        "seconds",
    )
    val SentryCooldown = Amount(
        "sentry.cooldown",
        SettingGroup.Sentry,
        "Between alarms",
        "After an alarm, how long before Sentry raises another.",
        30,
        10..600,
        10,
        "seconds",
    )
    val SentryRecords = Toggle(
        "sentry.record_on_camera",
        SettingGroup.Sentry,
        "Record on the camera",
        "Records to the camera's card while the alarm lasts, at the camera's full quality.",
        true,
    )
    val SentrySnapshots = Toggle(
        "sentry.snapshots",
        SettingGroup.Sentry,
        "Save a snapshot",
        "Saves the picture that raised the alarm to your gallery.",
        true,
    )

    val DeleteAfterCopy = Toggle(
        "library.delete_after_copy",
        SettingGroup.Library,
        "Delete from the camera after copying",
        "Frees the camera's card once a file is safely on the phone.",
        false,
    )

    val CheckForUpdates = Toggle(
        "updates.check",
        SettingGroup.Updates,
        "Check for updates",
        "Asks GitHub for a newer version when the app opens. Nothing else is sent.",
        true,
    )

    val OnboardingDone = Toggle(
        "app.onboarding_done",
        SettingGroup.Internal,
        "Onboarding",
        "Whether the first-run introduction has been finished.",
        false,
    )
    val LastVersion = Text(
        "app.last_version",
        SettingGroup.Internal,
        "Last version",
        "The version that last ran, so an update can say what changed.",
        null,
    ) { it.isNotBlank() }

    /** Every setting, in the order the settings screen lists them within their sections. */
    val all: List<Setting<*>> = listOf(
        AutoConnect,
        Reconnect,
        CameraPassword,
        CameraName,
        Fit,
        Grid,
        Flip,
        Mirror,
        KeepScreenOn,
        ShowStreamStats,
        Units,
        Layout,
        HudMirror,
        SentrySensitivity,
        SentryArmDelay,
        SentryCooldown,
        SentryRecords,
        SentrySnapshots,
        DeleteAfterCopy,
        CheckForUpdates,
        OnboardingDone,
        LastVersion,
    )

    /** The settings a person sees, by section. */
    val shown: Map<SettingGroup, List<Setting<*>>> = all.filter {
        it.group != SettingGroup.Internal
    }.groupBy { it.group }
}

/**
 * The app's settings, read and written by their typed keys.
 *
 * A value that is missing or cannot be read back, from an older version or a damaged file, is its
 * setting's default rather than an error: a person should never be locked out by a setting.
 */
class Settings(private val store: SettingsStore) {
    private val mutableChanges = MutableStateFlow(0L)

    /**
     * Counts every change to any setting, so a screen that shows many of them can redraw when one
     * changes without watching each.
     */
    val changes: StateFlow<Long> = mutableChanges.asStateFlow()

    /** What [setting] is set to. */
    operator fun <T> get(setting: Setting<T>): T = store.read(setting.key)?.let(setting::decode) ?: setting.default

    /**
     * Sets [setting] to [value].
     *
     * @throws IllegalArgumentException [value] is not one [setting] accepts.
     */
    operator fun <T> set(setting: Setting<T>, value: T) {
        require(setting.accepts(value)) { "$value is not a value $setting accepts." }
        store.write(setting.key, if (value == null) null else setting.encode(value))
        mutableChanges.value++
    }

    /** Puts [setting] back to its default. */
    fun reset(setting: Setting<*>) {
        store.write(setting.key, null)
        mutableChanges.value++
    }

    /** [setting]'s value now and every time it changes. */
    fun <T> watch(setting: Setting<T>): Flow<T> = changes.map { get(setting) }.distinctUntilChanged()
}
