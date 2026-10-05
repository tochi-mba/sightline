package com.rextechnologies.sightline.ui.live

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.core.camera.Problem
import com.rextechnologies.sightline.core.camera.Remedy
import com.rextechnologies.sightline.core.link.AdviceLevel
import com.rextechnologies.sightline.core.link.LinkAdvice
import com.rextechnologies.sightline.core.link.LinkAdvisor
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.design.AdvisoryBlock
import com.rextechnologies.sightline.design.InfoCard
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.PageHeading
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SignalButton
import com.rextechnologies.sightline.design.Tone
import com.rextechnologies.sightline.ui.Platform

/**
 * Before a camera is connected: which camera, what connecting will do to the phone's internet, and the
 * button that connects. While connecting, what Android is about to ask; after a failure, the one
 * sentence that explains it and the one button that helps.
 */
@Composable
fun ConnectPanel(graph: AppGraph, platform: Platform, camera: CameraState) {
    var advice by remember { mutableStateOf<LinkAdvice?>(null) }
    LaunchedEffect(camera.connection) {
        // Read afresh whenever the connection changes: Wi-Fi may have been switched on meanwhile.
        advice = LinkAdvisor.advise(graph.phoneNetworks())
    }

    val connect = {
        platform.withNearbyPermission {
            platform.startCameraService()
            graph.controller.connect(graph.cameraNetwork())
        }
    }
    val version by graph.settings.changes.collectAsState()
    val remembered = remember(version) { graph.settings[AppSettings.CameraName] }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(RexSpace.PageMargin),
        verticalArrangement = Arrangement.spacedBy(RexSpace.CardSpacing),
    ) {
        PageHeading(
            eyebrow = "Camera",
            headline = remembered ?: "Your camera",
            statusText = connectionWord(camera.connection),
            statusTone = if (camera.connection is Connection.Failed) Tone.Live else Tone.Neutral,
        )

        when (val connection = camera.connection) {
            is Connection.Joining -> Waiting(
                title = "Waiting for Android",
                body = if (connection.network.isExact) {
                    "Joining ${connection.network.name}. Android remembers this camera, so it should not need to ask."
                } else {
                    "Android lists the cameras it can see. Tap yours, then Connect."
                },
                onCancel = { graph.controller.disconnect() },
            )

            Connection.Opening -> Waiting(
                title = "Talking to the camera",
                body = "Reading its settings and what it is doing.",
                onCancel = { graph.controller.disconnect() },
            )

            is Connection.Failed -> ProblemCard(connection.problem, platform, connect)
            else -> Unit
        }

        advice?.let { AdviceCard(it, platform) }

        if (camera.connection == Connection.Idle || camera.connection is Connection.Failed) {
            SignalButton(text = "Connect to the camera", onClick = connect, modifier = Modifier.fillMaxWidth())
            if (remembered != null) {
                OutlineAction(
                    text = "Use a different camera",
                    onClick = { graph.settings[AppSettings.CameraName] = null },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }

        InfoCard {
            RexText(text = "Turn on the camera's Wi-Fi first", style = RexType.TitleMedium)
            RexText(
                text = "On most cameras of this kind, hold the Wi-Fi or Up button until the screen shows its " +
                    "Wi-Fi name and password. Its password is set in Settings if you changed it.",
                style = RexType.BodySmall,
            )
        }
    }
}

@Composable
private fun Waiting(title: String, body: String, onCancel: () -> Unit) {
    InfoCard(borderTone = Tone.Signal) {
        RexText(text = title, style = RexType.TitleMedium)
        RexText(text = body, style = RexType.BodySmall)
        OutlineAction(text = "Cancel", onClick = onCancel)
    }
}

@Composable
private fun ProblemCard(problem: Problem, platform: Platform, retry: () -> Unit) {
    InfoCard(borderTone = Tone.Live) {
        RexText(text = problem.explanation, style = RexType.TitleMedium)
        AdvisoryBlock(heading = "What happened", body = problem.detail)
        when (problem.kind.remedy) {
            Remedy.OpenWifiSettings -> OutlineAction(text = "Open Wi-Fi settings", onClick = platform::openWifiSettings)
            Remedy.OpenAppPermissions -> OutlineAction(text = "Open permissions", onClick = platform::openAppSettings)
            Remedy.TryAgain -> OutlineAction(text = "Try again", onClick = retry)
        }
    }
}

@Composable
private fun AdviceCard(advice: LinkAdvice, platform: Platform) {
    InfoCard(borderTone = toneOf(advice.level)) {
        RexText(text = "Your internet", style = RexType.LabelSmall)
        advice.sentences.forEach { RexText(text = it, style = RexType.BodyMedium) }
        if (advice.level == AdviceLevel.Blocked) {
            OutlineAction(text = "Open Wi-Fi settings", onClick = platform::openWifiSettings)
        }
    }
}

/** How a piece of advice is framed: nothing lost is quiet, losing something is Live. */
fun toneOf(level: AdviceLevel): Tone = when (level) {
    AdviceLevel.Fine -> Tone.Line
    AdviceLevel.Note -> Tone.Neutral
    AdviceLevel.Warning, AdviceLevel.Blocked -> Tone.Live
}

/** One word on the connection, for the heading's pill, or null when there is nothing to say. */
fun connectionWord(connection: Connection): String? = when (connection) {
    Connection.Idle -> null
    is Connection.Joining, Connection.Opening -> "Connecting"
    Connection.Connected -> "Connected"
    is Connection.Reconnecting -> "Reconnecting"
    is Connection.Failed -> "Not connected"
}
