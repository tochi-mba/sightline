package com.rextechnologies.sightline.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.core.navigation.Back
import com.rextechnologies.sightline.core.navigation.BackStack
import com.rextechnologies.sightline.core.navigation.Destination
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.updates.WhatsNewCatalog
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.rexClickable
import com.rextechnologies.sightline.ui.library.LibraryScreen
import com.rextechnologies.sightline.ui.live.LiveScreen
import com.rextechnologies.sightline.ui.onboarding.OnboardingScreen
import com.rextechnologies.sightline.ui.onboarding.WhatsNewScreen
import com.rextechnologies.sightline.ui.sentry.SentryScreen
import com.rextechnologies.sightline.ui.settings.SettingsScreen
import java.util.Locale

/**
 * What the screens ask the phone to do, which only the activity can: permissions, system settings,
 * links, the screen staying on, and the service that keeps a connection alive.
 */
interface Platform {
    /** Runs [then] once the app may look for nearby Wi-Fi, asking first where Android needs it. */
    fun withNearbyPermission(then: () -> Unit)

    /** Runs [then] once the app may read the GPS, asking first. */
    fun withLocationPermission(then: () -> Unit)

    /** Runs [then] once the app may post notifications, asking first where Android needs it. */
    fun withNotificationPermission(then: () -> Unit)

    fun openWifiSettings()

    fun openAppSettings()

    fun openLink(url: String)

    /** Keeps the screen on while [on], for the live picture. */
    fun keepScreenOn(on: Boolean)

    /** Starts the service that keeps the app alive while a camera is connected or Sentry is armed. */
    fun startCameraService()
}

/**
 * The whole app: the introduction on a first run, what changed after an update that earned a word, and
 * otherwise the four places, with a bar along the bottom of a phone and a rail down the side of anything
 * wider.
 */
@Composable
fun SightlineApp(graph: AppGraph, platform: Platform) {
    val settings = graph.settings
    val version by settings.changes.collectAsState()
    // Read from settings rather than held here, so "show the introduction again" in Settings takes effect.
    val onboarded = remember(version) { settings[AppSettings.OnboardingDone] }
    // Worked out once, at launch: an update is greeted on the first run after it, not again.
    var whatsNew by remember {
        mutableStateOf(
            if (settings[AppSettings.OnboardingDone]) {
                WhatsNewCatalog.since(settings[AppSettings.LastVersion], graph.version, graph.releaseNotes)
            } else {
                emptyList()
            },
        )
    }

    Box(Modifier.fillMaxSize().background(RexColors.Ink).windowInsetsPadding(WindowInsets.safeDrawing)) {
        when {
            !onboarded -> OnboardingScreen(
                onFinish = {
                    settings[AppSettings.OnboardingDone] = true
                    settings[AppSettings.LastVersion] = graph.version
                },
            )

            whatsNew.isNotEmpty() -> WhatsNewScreen(
                entries = whatsNew,
                onContinue = {
                    settings[AppSettings.LastVersion] = graph.version
                    whatsNew = emptyList()
                },
            )

            else -> {
                LaunchedEffect(graph.version) {
                    // Nothing to say about this version: it is recorded, so the next update compares from here.
                    settings[AppSettings.LastVersion] = graph.version
                }
                Places(graph, platform)
            }
        }
    }
}

@Composable
private fun Places(graph: AppGraph, platform: Platform) {
    var saved by rememberSaveable { mutableStateOf(BackStack.Initial.save()) }
    val stack = BackStack.restore(saved)
    val go: (BackStack) -> Unit = { saved = it.save() }

    LaunchedEffect(Unit) {
        // A camera used before is joined again as the app opens, when the person wants that.
        val settings = graph.settings
        if (settings[AppSettings.AutoConnect] && settings[AppSettings.CameraName] != null &&
            graph.controller.state.value.connection == Connection.Idle
        ) {
            platform.withNearbyPermission {
                platform.startCameraService()
                graph.controller.connect(graph.cameraNetwork())
            }
        }
    }

    BackHandler(enabled = stack.canGoBack) {
        (stack.pop() as? Back.To)?.let { go(it.stack) }
    }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val wide = maxWidth >= WIDE
        if (wide) {
            Row(Modifier.fillMaxSize()) {
                NavigationList(stack.current, vertical = true, Modifier.width(132.dp).fillMaxHeight()) {
                    go(stack.select(it))
                }
                Box(Modifier.weight(1f)) { Place(stack.current, graph, platform, wide) }
            }
        } else {
            Column(Modifier.fillMaxSize()) {
                Box(Modifier.weight(1f)) { Place(stack.current, graph, platform, wide) }
                NavigationList(stack.current, vertical = false, Modifier.fillMaxWidth()) { go(stack.select(it)) }
            }
        }
    }
}

@Composable
private fun Place(destination: Destination, graph: AppGraph, platform: Platform, wide: Boolean) {
    val camera by graph.controller.state.collectAsState()
    when (destination) {
        Destination.Live -> LiveScreen(graph, platform, camera, wide)
        Destination.Library -> LibraryScreen(graph, camera)
        Destination.Sentry -> SentryScreen(graph, platform, camera)
        Destination.Settings -> SettingsScreen(graph, platform, camera)
    }
}

/** The four places, as a bar or a rail. The current one is marked in Signal and announced as selected. */
@Composable
private fun NavigationList(current: Destination, vertical: Boolean, modifier: Modifier, select: (Destination) -> Unit) {
    val items: @Composable (Modifier) -> Unit = { itemModifier ->
        Destination.entries.forEach { destination ->
            val chosen = destination == current
            Box(
                modifier = itemModifier
                    .semantics { selected = chosen }
                    .rexClickable(role = Role.Tab) { select(destination) }
                    .defaultMinSize(minHeight = RexSpace.TouchTarget)
                    .padding(horizontal = RexSpace.Small),
                contentAlignment = Alignment.Center,
            ) {
                RexText(
                    text = labelFor(destination).uppercase(Locale.ROOT),
                    style = RexType.LabelSmall.copy(color = if (chosen) RexColors.Signal else RexColors.Muted),
                    maxLines = 1,
                )
            }
        }
    }

    if (vertical) {
        Column(
            modifier.background(RexColors.Panel).padding(vertical = RexSpace.Large),
            verticalArrangement = Arrangement.spacedBy(RexSpace.Small),
        ) { items(Modifier.fillMaxWidth()) }
    } else {
        Row(modifier.background(RexColors.Panel).padding(vertical = RexSpace.Tiny)) { items(Modifier.weight(1f)) }
    }
}

/** What each place is called in the navigation. */
fun labelFor(destination: Destination): String = when (destination) {
    Destination.Live -> "Live"
    Destination.Library -> "Card"
    Destination.Sentry -> "Sentry"
    Destination.Settings -> "Settings"
}

/** From this width a rail down the side replaces the bar along the bottom. */
private val WIDE = 600.dp
