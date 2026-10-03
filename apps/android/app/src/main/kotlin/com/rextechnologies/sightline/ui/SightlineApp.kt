package com.rextechnologies.sightline.ui

import android.graphics.BitmapFactory
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rextechnologies.sightline.camera.CameraService
import com.rextechnologies.sightline.core.model.AppModel
import com.rextechnologies.sightline.core.model.ConnectionState
import com.rextechnologies.sightline.core.navigation.Destination
import com.rextechnologies.sightline.core.onboarding.OnboardingPage
import com.rextechnologies.sightline.core.onboarding.OnboardingState
import com.rextechnologies.sightline.core.settings.byCategory
import com.rextechnologies.sightline.design.Eyebrow
import com.rextechnologies.sightline.design.RexButton
import com.rextechnologies.sightline.design.RexInk
import com.rextechnologies.sightline.design.RexLine
import com.rextechnologies.sightline.design.RexLive
import com.rextechnologies.sightline.design.RexMuted
import com.rextechnologies.sightline.design.RexPanel
import com.rextechnologies.sightline.design.RexRaised
import com.rextechnologies.sightline.design.RexSignal
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.StatRow
import com.rextechnologies.sightline.protocol.gpsock.MenuSetting
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind

@Composable
fun SightlineApp(
    service: CameraService?,
    onboardingComplete: Boolean,
    onFinishOnboarding: () -> Unit,
    onConnect: (String, String) -> Unit,
    onHudChanged: (Boolean) -> Unit,
    onStartSentry: () -> Unit,
    onStopSentry: () -> Unit,
) {
    if (!onboardingComplete) {
        Onboarding(onFinishOnboarding)
        return
    }

    val model = if (service == null) AppModel() else service.state.collectAsState().value
    var destinationName by rememberSaveable { mutableStateOf(Destination.Start.name) }
    val destination = Destination.entries.firstOrNull { it.name == destinationName } ?: Destination.Start

    Column(Modifier.fillMaxSize().background(RexInk)) {
        Header(model)
        BoxWithConstraints(Modifier.weight(1f)) {
            if (maxWidth >= 760.dp) {
                Row(Modifier.fillMaxSize()) {
                    Navigation(destination, { destinationName = it.name }, Modifier.width(180.dp).fillMaxHeight())
                    Screen(destination, model, service, onConnect, onHudChanged, onStartSentry, onStopSentry)
                }
            } else {
                Column(Modifier.fillMaxSize()) {
                    Box(Modifier.weight(1f)) {
                        Screen(destination, model, service, onConnect, onHudChanged, onStartSentry, onStopSentry)
                    }
                    Navigation(destination, { destinationName = it.name }, Modifier.fillMaxWidth())
                }
            }
        }
    }
}

@Composable
private fun Header(model: AppModel) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Eyebrow("REX TECHNOLOGIES")
            Text("SIGHTLINE", color = RexText, fontSize = 24.sp, fontWeight = FontWeight.Black, letterSpacing = 2.sp)
        }
        Box(Modifier.size(9.dp).clip(CircleShape).background(if (model.connected) RexSignal else RexMuted))
        Spacer(Modifier.width(8.dp))
        Text(if (model.connected) "LOCAL LINK" else "OFFLINE", color = RexMuted, fontSize = 12.sp)
    }
}

@Composable
private fun Navigation(current: Destination, select: (Destination) -> Unit, modifier: Modifier) {
    Row(
        modifier.background(RexRaised).horizontalScroll(rememberScrollState()).padding(8.dp),
        horizontalArrangement = Arrangement.SpaceEvenly,
    ) {
        Destination.entries.forEach { destination ->
            Text(
                destination.name.uppercase(),
                modifier = Modifier
                    .clip(RoundedCornerShape(12.dp))
                    .background(if (destination == current) RexSignal else Color.Transparent)
                    .clickable { select(destination) }
                    .padding(horizontal = 18.dp, vertical = 12.dp),
                color = if (destination == current) RexInk else RexMuted,
                fontWeight = FontWeight.Bold,
                fontSize = 12.sp,
            )
        }
    }
}

@Composable
private fun Screen(
    destination: Destination,
    model: AppModel,
    service: CameraService?,
    onConnect: (String, String) -> Unit,
    onHudChanged: (Boolean) -> Unit,
    onStartSentry: () -> Unit,
    onStopSentry: () -> Unit,
) {
    when (destination) {
        Destination.Live -> Live(model, service, onConnect, onHudChanged)
        Destination.Library -> Library(model, service)
        Destination.Sentry -> Sentry(model, onStartSentry, onStopSentry)
        Destination.Settings -> Settings(model, service)
    }
}

@Composable
private fun Live(
    model: AppModel,
    service: CameraService?,
    onConnect: (String, String) -> Unit,
    onHudChanged: (Boolean) -> Unit,
) {
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item {
            if (model.connected) LivePicture(model) else ConnectPanel(model.connection, service != null, onConnect)
        }
        if (model.connected) {
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    RexButton("TAKE PHOTO", { service?.capture() }, Modifier.weight(1f))
                    RexButton(
                        if (model.recording) "STOP REC" else "RECORD",
                        { service?.toggleRecording() },
                        Modifier.weight(1f),
                        accent = if (model.recording) RexLive else RexSignal,
                    )
                }
            }
            item {
                RexButton(
                    if (model.hudVisible) "HIDE RIDE HUD" else "SHOW RIDE HUD",
                    { onHudChanged(!model.hudVisible) },
                    Modifier.fillMaxWidth(),
                    accent = RexRaised,
                )
            }
            item { RexButton("DISCONNECT", { service?.disconnect() }, Modifier.fillMaxWidth(), accent = RexRaised) }
        }
        model.message?.let { message -> item { Text(message, color = RexMuted) } }
    }
}

@Composable
private fun LivePicture(model: AppModel) {
    RexPanel(Modifier.fillMaxWidth()) {
        Box(
            Modifier.fillMaxWidth().aspectRatio(16f / 9f).background(Color.Black),
            contentAlignment = Alignment.Center,
        ) {
            val image = remember(model.jpeg) {
                model.jpeg?.let { bytes -> BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap() }
            }
            if (image == null) {
                CircularProgressIndicator(color = RexSignal)
            } else {
                androidx.compose.foundation.Image(
                    bitmap = image,
                    contentDescription = "Live camera view",
                    modifier = Modifier.fillMaxSize(),
                    contentScale = ContentScale.Fit,
                )
            }
            if (model.recording) {
                Text(
                    "● REC",
                    color = RexLive,
                    fontWeight = FontWeight.Black,
                    modifier = Modifier.align(Alignment.TopStart).padding(12.dp),
                )
            }
            if (model.hudVisible) {
                Column(
                    Modifier.align(Alignment.BottomCenter).fillMaxWidth().background(Color(0x99080A09)).padding(12.dp),
                ) {
                    StatRow(
                        "SPEED" to "${model.hud.speedText} km/h",
                        "ALT" to model.hud.altitudeText,
                        "HEAD" to model.hud.headingText,
                    )
                }
            }
        }
        Spacer(Modifier.height(10.dp))
        StatRow("STREAM" to "%.1f fps".format(model.framesPerSecond), "ROUTE" to "LOCAL ONLY")
    }
}

@Composable
private fun ConnectPanel(state: ConnectionState, serviceReady: Boolean, connect: (String, String) -> Unit) {
    var network by rememberSaveable { mutableStateOf("ActionCam_*") }
    var password by rememberSaveable { mutableStateOf("12345678") }
    RexPanel(Modifier.fillMaxWidth()) {
        Eyebrow("LOCAL CAMERA LINK")
        Text(
            "Keep your internet. Add the camera beside it.",
            color = RexText,
            fontSize = 26.sp,
            fontWeight = FontWeight.Black,
        )
        Text(
            "Android shows a Wi-Fi consent sheet. Sightline requests a local-only network and binds only camera sockets to it; mobile data and hotspot remain the default route.",
            color = RexMuted,
            modifier = Modifier.padding(vertical = 12.dp),
        )
        Field("CAMERA WI-FI", network, { network = it })
        Spacer(Modifier.height(8.dp))
        Field("PASSWORD", password, { password = it }, password = true)
        Spacer(Modifier.height(14.dp))
        RexButton(
            if (state is ConnectionState.Connecting) "WAITING FOR ANDROID…" else "CONNECT LOCALLY",
            { connect(network, password) },
            Modifier.fillMaxWidth(),
            enabled = serviceReady && state !is ConnectionState.Connecting,
        )
        if (state is ConnectionState.Failed) {
            Text(
                state.message,
                color = RexLive,
                modifier = Modifier.padding(top = 10.dp),
            )
        }
    }
}

@Composable
private fun Field(label: String, value: String, changed: (String) -> Unit, password: Boolean = false) {
    Eyebrow(label, RexMuted)
    BasicTextField(
        value = value,
        onValueChange = changed,
        modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(RexRaised).padding(14.dp),
        textStyle = TextStyle(color = RexText, fontSize = 16.sp),
        singleLine = true,
        visualTransformation = if (password) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
    )
}

@Composable
private fun Library(model: AppModel, service: CameraService?) {
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        RexPanel(Modifier.fillMaxWidth()) {
            Eyebrow("CAMERA CARD")
            Text(
                model.library.count?.toString() ?: "—",
                color = RexText,
                fontSize = 64.sp,
                fontWeight = FontWeight.Black,
            )
            Text("files reported by the camera", color = RexMuted)
            Spacer(Modifier.height(14.dp))
            RexButton(
                if (model.library.loading) "READING CARD…" else "REFRESH LIBRARY",
                { service?.refreshLibrary() },
                Modifier.fillMaxWidth(),
                enabled = model.connected && !model.library.loading,
            )
        }
        Text(
            "Sightline reports only facts this firmware supplies reliably. File names, thumbnails and downloads appear when the camera returns a valid list.",
            color = RexMuted,
        )
    }
}

@Composable
private fun Sentry(model: AppModel, start: () -> Unit, stop: () -> Unit) {
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        RexPanel(Modifier.fillMaxWidth()) {
            Eyebrow(if (model.sentryRunning) "WATCHING" else "BACKGROUND SECURITY")
            Text(
                if (model.sentryRunning) "Sentry is active" else "Turn this camera into a lookout.",
                color = RexText,
                fontSize = 28.sp,
                fontWeight = FontWeight.Black,
            )
            Text(
                "A visible Android notification keeps the local camera link alive. Motion is measured from live frames; no cloud or internet route is used.",
                color = RexMuted,
                modifier = Modifier.padding(vertical = 12.dp),
            )
            StatRow(
                "MOTION EVENTS" to model.motionEvents.toString(),
                "LINK" to if (model.connected) "READY" else "CONNECT FIRST",
            )
            Spacer(Modifier.height(14.dp))
            RexButton(
                if (model.sentryRunning) "STOP SENTRY" else "START SENTRY",
                if (model.sentryRunning) stop else start,
                Modifier.fillMaxWidth(),
                enabled = model.connected,
                accent = if (model.sentryRunning) RexLive else RexSignal,
            )
        }
    }
}

@Composable
private fun Settings(model: AppModel, service: CameraService?) {
    val groups = model.settings.byCategory().entries.toList()
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (groups.isEmpty()) item { Text("Connect to read this camera's settings.", color = RexMuted) }
        groups.forEach { (category, settings) ->
            item { Eyebrow(category) }
            items(settings, key = { setting -> setting.id }) { setting -> Setting(setting, service) }
        }
    }
}

@Composable
private fun Setting(setting: MenuSetting, service: CameraService?) {
    RexPanel(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(setting.name, color = RexText, fontWeight = FontWeight.Bold, fontSize = 17.sp)
                Text(setting.kind?.name ?: "Unknown type", color = RexMuted, fontSize = 12.sp)
            }
            Text("0x${setting.id.toString(16).padStart(4, '0')}", color = RexMuted, fontSize = 11.sp)
        }
        if (setting.kind == MenuSettingKind.Choice) {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 10.dp)) {
                items(setting.choices, key = { choice -> choice.value }) { choice ->
                    RexButton(choice.label, { service?.writeSetting(setting.id, choice.value) }, accent = RexRaised)
                }
            }
        } else {
            Text(
                when (setting.kind) {
                    MenuSettingKind.Action -> "Safety action — available from the camera itself."
                    MenuSettingKind.Text -> "Text value — shown from the camera catalogue."
                    MenuSettingKind.ReadOnly -> "Read-only camera information."
                    else -> "This firmware did not identify how this value is written."
                },
                color = RexMuted,
                modifier = Modifier.padding(top = 8.dp),
            )
        }
    }
}

@Composable
private fun Onboarding(finish: () -> Unit) {
    var state by remember { mutableStateOf(OnboardingState()) }
    val (eyebrow, title, body) = when (state.page) {
        OnboardingPage.Welcome -> Triple(
            "WELCOME TO SIGHTLINE",
            "Your camera, without the vendor-app compromise.",
            "Live view, ride HUD, camera settings, card library and background Sentry live in one deliberately local app.",
        )
        OnboardingPage.InternetStaysOn -> Triple(
            "THE IMPORTANT BIT",
            "Your mobile data and hotspot stay on.",
            "Sightline never toggles Wi-Fi, disables mobile data, or binds the whole app process. Android adds the camera as a local-only network; only camera sockets use it.",
        )
        OnboardingPage.CameraConsent -> Triple(
            "ANDROID CONSENT",
            "Approve the camera network when Android asks.",
            "The system owns the Wi-Fi picker. A network without internet is expected here and will not become your phone's default route.",
        )
        OnboardingPage.Ready -> Triple(
            "READY",
            "Wake the camera and keep it nearby.",
            "Its default network is ActionCam_* with password 12345678. You can change both before connecting.",
        )
    }
    Box(Modifier.fillMaxSize().background(RexInk).padding(24.dp), contentAlignment = Alignment.Center) {
        RexPanel(Modifier.fillMaxWidth()) {
            Eyebrow(eyebrow)
            Text(
                title,
                color = RexText,
                fontSize = 32.sp,
                fontWeight = FontWeight.Black,
                modifier = Modifier.padding(vertical = 14.dp),
            )
            Text(body, color = RexMuted, fontSize = 17.sp, lineHeight = 25.sp)
            HorizontalDivider(Modifier.padding(vertical = 18.dp), color = RexLine)
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                if (state.page != OnboardingPage.Welcome) {
                    RexButton("BACK", { state = state.previous() }, Modifier.weight(1f), accent = RexRaised)
                }
                RexButton(
                    if (state.page == OnboardingPage.Ready) "OPEN SIGHTLINE" else "CONTINUE",
                    {
                        if (state.page == OnboardingPage.Ready) finish() else state = state.next()
                    },
                    Modifier.weight(1f),
                )
            }
        }
    }
}
