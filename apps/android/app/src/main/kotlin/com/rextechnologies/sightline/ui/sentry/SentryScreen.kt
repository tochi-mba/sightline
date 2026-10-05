package com.rextechnologies.sightline.ui.sentry

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.sentry.SentryAlarm
import com.rextechnologies.sightline.core.sentry.SentryState
import com.rextechnologies.sightline.core.sentry.SentryStatus
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.design.AdvisoryBlock
import com.rextechnologies.sightline.design.InfoCard
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.PageHeading
import com.rextechnologies.sightline.design.ProgressTrack
import com.rextechnologies.sightline.design.RexShapes
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SectionLabel
import com.rextechnologies.sightline.design.SignalButton
import com.rextechnologies.sightline.design.Tone
import com.rextechnologies.sightline.ui.Platform
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * Sentry: the camera as a lookout. Armed, it watches the live picture for movement with the screen off,
 * and says when something moves.
 */
@Composable
fun SentryScreen(graph: AppGraph, platform: Platform, camera: CameraState) {
    val status by graph.sentry.status.collectAsState()
    val version by graph.settings.changes.collectAsState()
    val summary = remember(version) { settingsSummary(graph) }

    LazyColumn(
        Modifier.fillMaxSize().padding(horizontal = RexSpace.PageMargin),
        verticalArrangement = Arrangement.spacedBy(RexSpace.CardSpacing),
    ) {
        item {
            PageHeading(
                eyebrow = "Sentry",
                headline = headline(status),
                statusText = stateWord(status.watch),
                statusTone = if (status.watch is SentryState.Alarm) {
                    Tone.Live
                } else if (status.armed) {
                    Tone.Signal
                } else {
                    Tone.Neutral
                },
                modifier = Modifier.padding(top = RexSpace.Medium),
            )
        }

        item {
            InfoCard(borderTone = if (status.armed) Tone.Signal else Tone.Line) {
                if (status.armed) {
                    RexText(text = "Movement now", style = RexType.LabelSmall)
                    ProgressTrack(fraction = (status.score / FULL_SCALE).toFloat(), contentDescription = "Movement now")
                    if (!camera.isConnected) {
                        AdvisoryBlock(
                            "Waiting for the camera",
                            "Sentry starts watching as soon as a camera is connected.",
                        )
                    }
                    OutlineAction(text = "Disarm", onClick = {
                        graph.sentry.disarm()
                    }, tone = Tone.Live, modifier = Modifier.fillMaxWidth())
                } else {
                    RexText(
                        text =
                        "Sightline watches the camera's picture for movement, with your screen off, and tells you " +
                            "when something moves. Nothing leaves your phone.",
                        style = RexType.BodyMedium,
                    )
                    RexText(text = summary, style = RexType.BodySmall)
                    SignalButton(
                        text = "Arm Sentry",
                        onClick = {
                            platform.withNotificationPermission {
                                platform.startCameraService()
                                graph.sentry.arm()
                            }
                        },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
        }

        item {
            InfoCard {
                RexText(text = "For a long watch", style = RexType.TitleMedium)
                RexText(
                    text =
                    "Plug the camera into USB power and keep the phone charging. The camera's Wi-Fi stays on while " +
                        "Sightline is connected; if it goes to sleep, Sightline reconnects.",
                    style = RexType.BodySmall,
                )
            }
        }

        if (status.alarms.isNotEmpty()) {
            item { SectionLabel(text = "Alarms", modifier = Modifier.padding(top = RexSpace.Small)) }
            items(status.alarms, key = { it.number }) { AlarmRow(it) }
        }
    }
}

@Composable
private fun AlarmRow(alarm: SentryAlarm) {
    val picture =
        remember(alarm.number) {
            BitmapFactory.decodeByteArray(alarm.snapshot, 0, alarm.snapshot.size)?.asImageBitmap()
        }
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(RexSpace.Compact),
    ) {
        picture?.let {
            Image(
                it,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.size(width = 96.dp, height = 54.dp),
            )
        }
        Column {
            RexText(
                text = "Movement at ${TIME.withZone(ZoneId.systemDefault()).format(alarm.at)}",
                style = RexType.TitleMedium,
            )
            RexText(text = DAY.withZone(ZoneId.systemDefault()).format(alarm.at), style = RexType.BodySmall)
        }
    }
}

/** The settings Sentry will arm with, in a line. */
private fun settingsSummary(graph: AppGraph): String {
    val settings = graph.settings
    val sensitivity = settings[AppSettings.SentrySensitivity].name.lowercase(Locale.ROOT)
    val delay = settings[AppSettings.SentryArmDelay]
    val records = if (settings[AppSettings.SentryRecords]) "records on the camera" else "does not record"
    return "Sensitivity $sensitivity, starts after $delay seconds, and $records when it sees movement. " +
        "Change these in Settings."
}

/** The heading for [status]. */
fun headline(status: SentryStatus): String = when {
    status.watch is SentryState.Alarm -> "Movement"
    status.armed -> "Watching"
    else -> "Not armed"
}

/** One word on the watch, for the heading's pill. */
fun stateWord(watch: SentryState): String = when (watch) {
    SentryState.Disarmed -> "Off"
    SentryState.Arming -> "Arming"
    SentryState.Watching -> "Watching"
    is SentryState.Alarm -> "Alarm"
    SentryState.Cooldown -> "Settling"
}

/** A fifth of the picture changing fills the meter: more than any sensitivity needs to raise the alarm. */
private const val FULL_SCALE = 0.2
private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm:ss", Locale.ENGLISH)
private val DAY: DateTimeFormatter = DateTimeFormatter.ofPattern("EEEE d MMMM", Locale.ENGLISH)
