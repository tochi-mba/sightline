package com.rextechnologies.sightline.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.camera.CameraSetting
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.settings.Amount
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.Choice
import com.rextechnologies.sightline.core.settings.Password
import com.rextechnologies.sightline.core.settings.Setting
import com.rextechnologies.sightline.core.settings.Settings
import com.rextechnologies.sightline.core.settings.Text
import com.rextechnologies.sightline.core.settings.Toggle
import com.rextechnologies.sightline.core.settings.byCategory
import com.rextechnologies.sightline.design.Divider
import com.rextechnologies.sightline.design.OptionRow
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.PageHeading
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexDialog
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SectionLabel
import com.rextechnologies.sightline.design.SettingRow
import com.rextechnologies.sightline.design.SignalButton
import com.rextechnologies.sightline.design.Stepper
import com.rextechnologies.sightline.design.TextEntry
import com.rextechnologies.sightline.design.ToggleSwitch
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import com.rextechnologies.sightline.ui.Platform

/** Where the source lives, linked from About. */
const val SOURCE_URL = "https://github.com/tochi-mba/sightline"

/**
 * The camera's own settings, from its menu, while one is connected; then the app's, drawn from its typed
 * list; then what the app is and what it never does.
 */
@Composable
fun SettingsScreen(graph: AppGraph, platform: Platform, camera: CameraState) {
    val version by graph.settings.changes.collectAsState()
    val update by graph.update.collectAsState()
    var choosingCamera by remember { mutableStateOf<CameraSetting?>(null) }
    var choosingApp by remember { mutableStateOf<Choice<*>?>(null) }
    var editing by remember { mutableStateOf<Password?>(null) }

    LazyColumn(
        Modifier.fillMaxSize().padding(horizontal = RexSpace.PageMargin),
        verticalArrangement = Arrangement.spacedBy(RexSpace.Tiny),
    ) {
        item {
            PageHeading(
                eyebrow = "Settings",
                headline = "Settings",
                modifier = Modifier.padding(vertical = RexSpace.Medium),
            )
        }

        item { SectionLabel("Camera", Modifier.padding(top = RexSpace.Small)) }
        if (!camera.isConnected) {
            item { RexText("Connect a camera to change its own settings.", style = RexType.BodySmall) }
        } else {
            camera.settings.byCategory().forEach { (category, settings) ->
                item(key = "category-$category") {
                    RexText(category, style = RexType.TitleMedium, modifier = Modifier.padding(top = RexSpace.Small))
                }
                settings.forEach { setting ->
                    item(key = "camera-${setting.menu.id}") {
                        CameraSettingRow(setting, enabled = camera.task == null && !camera.isRecording) {
                            choosingCamera =
                                setting
                        }
                    }
                }
            }
            if (camera.isRecording) {
                item { RexText("The camera's settings are locked while it records.", style = RexType.BodySmall) }
            }
        }

        AppSettings.shown.forEach { (group, settings) ->
            item(key = "group-$group") {
                Divider(Modifier.padding(top = RexSpace.Medium))
                SectionLabel(group.title, Modifier.padding(top = RexSpace.Medium))
            }
            settings.forEach { setting ->
                item(key = setting.key) {
                    val edited = editing
                    if (edited != null && edited == setting) {
                        PasswordEditor(edited, graph.settings) { editing = null }
                    } else {
                        // Settings are read, not observed, so the row is rebuilt whenever any setting changes.
                        key(version) {
                            AppSettingRow(setting, graph.settings, choose = {
                                choosingApp = it
                            }, edit = { editing = it })
                        }
                    }
                }
            }
        }

        item {
            Divider(Modifier.padding(top = RexSpace.Medium))
            SectionLabel("About", Modifier.padding(top = RexSpace.Medium))
            SettingRow(title = "Sightline", summary = "REX Technologies", value = graph.version)
            update?.let { release ->
                SettingRow(
                    title = "Version ${release.version} is available",
                    summary = "Download it from GitHub; it installs over this one and keeps your settings.",
                    value = "Get it",
                    onClick = { platform.openLink(release.page) },
                )
            }
            SettingRow(
                title = "Show the introduction again",
                onClick = { graph.settings[AppSettings.OnboardingDone] = false },
            )
            SettingRow(title = "Source code", summary = SOURCE_URL, onClick = { platform.openLink(SOURCE_URL) })
            RexText(
                text = "No account, no analytics, no ads. The camera's pictures stay between it and this phone; the " +
                    "only thing Sightline sends anywhere is a check for a newer version, which can be turned off above.",
                style = RexType.BodySmall,
                modifier = Modifier.padding(vertical = RexSpace.Medium),
            )
        }
    }

    choosingCamera?.let { setting ->
        RexDialog(title = setting.menu.name, onDismiss = { choosingCamera = null }) {
            setting.menu.choices.forEach { choice ->
                OptionRow(text = choice.label, selected = choice.value == setting.value, onClick = {
                    graph.controller.changeSetting(setting.menu.id, choice.value)
                    choosingCamera = null
                })
            }
        }
    }

    choosingApp?.let { setting -> ChoiceDialog(setting, graph.settings) { choosingApp = null } }
}

@Composable
private fun CameraSettingRow(setting: CameraSetting, enabled: Boolean, onClick: () -> Unit) {
    SettingRow(
        title = setting.menu.name,
        summary = cameraSummary(setting),
        value = setting.shown,
        enabled = enabled,
        onClick = if (setting.isChangeable) onClick else null,
    )
}

/** What a camera setting's row says under its name, for those that cannot be changed here. */
fun cameraSummary(setting: CameraSetting): String? = when {
    setting.isChangeable -> null
    setting.menu.kind == MenuSettingKind.Action -> "On the camera's own menu."
    else -> "Shown as the camera reports it; change it on the camera."
}

@Composable
private fun AppSettingRow(
    setting: Setting<*>,
    settings: Settings,
    choose: (Choice<*>) -> Unit,
    edit: (Password) -> Unit,
) {
    when (setting) {
        // What the app records for itself, such as the last camera's name; never listed.
        is Text -> Unit

        is Toggle -> SettingRow(
            title = setting.title,
            summary = setting.summary,
            onClick = { settings[setting] = !settings[setting] },
            trailing = {
                ToggleSwitch(checked = settings[setting], onCheckedChange = {
                    settings[setting] = it
                }, contentDescription = setting.title)
            },
        )

        is Choice<*> -> SettingRow(
            title = setting.title,
            summary = setting.summary,
            value = setting.labels[setting.options.indexOf(settings[setting])],
            onClick = { choose(setting) },
        )

        is Amount -> SettingRow(
            title = setting.title,
            summary = setting.summary,
            trailing = {
                Stepper(settings[setting], setting.range, setting.step, setting.unit, onChange = {
                    settings[setting] =
                        it
                })
            },
        )

        is Password -> SettingRow(
            title = setting.title,
            summary = setting.summary,
            value = "Change",
            onClick = { edit(setting) },
        )
    }
}

@Composable
private fun <E : Enum<E>> ChoiceDialog(setting: Choice<E>, settings: Settings, close: () -> Unit) {
    RexDialog(title = setting.title, body = setting.summary, onDismiss = close) {
        setting.options.forEachIndexed { index, option ->
            OptionRow(text = setting.labels[index], selected = settings[setting] == option, onClick = {
                settings[setting] = option
                close()
            })
        }
    }
}

/**
 * A password changed in place, under its own row rather than in a dialog: the keyboard then covers
 * nothing that matters, and the rule for what it accepts sits beside the field while somebody types.
 */
@Composable
private fun PasswordEditor(setting: Password, settings: Settings, close: () -> Unit) {
    var entered by remember { mutableStateOf(settings[setting]) }
    val valid = setting.accepts(entered)
    Column(
        Modifier.fillMaxWidth().padding(vertical = RexSpace.Small),
        verticalArrangement = Arrangement.spacedBy(RexSpace.Small),
    ) {
        RexText(setting.title, style = RexType.TitleMedium)
        RexText(setting.summary, style = RexType.BodySmall)
        TextEntry(
            value = entered,
            onValueChange = { entered = it },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            visualTransformation = PasswordVisualTransformation(),
            contentDescription = setting.title,
        )
        if (!valid) {
            RexText(setting.rule, style = RexType.BodySmall.copy(color = RexColors.Live))
        }
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(RexSpace.Small, Alignment.End),
        ) {
            OutlineAction(text = "Cancel", onClick = close)
            SignalButton(text = "Save", enabled = valid, onClick = {
                settings[setting] = entered
                close()
            })
        }
    }
}
