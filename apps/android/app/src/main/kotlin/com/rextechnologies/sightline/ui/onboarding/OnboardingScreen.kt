package com.rextechnologies.sightline.ui.onboarding

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.rextechnologies.sightline.core.onboarding.OnboardingPage
import com.rextechnologies.sightline.core.onboarding.OnboardingState
import com.rextechnologies.sightline.design.AdvisoryBlock
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SignalButton
import java.util.Locale

/** One page of the introduction: what it is called, what it says, and the one thing to remember. */
data class IntroPage(val eyebrow: String, val title: String, val body: String, val note: Pair<String, String>?)

/** The introduction's words, page by page. */
fun introFor(page: OnboardingPage): IntroPage = when (page) {
    OnboardingPage.Welcome -> IntroPage(
        "Welcome to Sightline",
        "See what your camera sees.",
        "The live picture, every setting your camera has, its card, a ride HUD and a security watch, in one app " +
            "that talks to the camera directly.",
        "No account" to "Nothing to sign up for. The camera's pictures stay between it and this phone.",
    )

    OnboardingPage.InternetStaysOn -> IntroPage(
        "The important bit",
        "Your internet stays on.",
        "Android adds the camera's Wi-Fi alongside your connection, for Sightline's use only. Mobile data and your " +
            "hotspot carry on as before, and Sightline never switches either off.",
        "On home Wi-Fi" to "Most phones can only join one Wi-Fi network, so your home Wi-Fi pauses while the camera " +
            "is connected. Sightline says so before it happens.",
    )

    OnboardingPage.CameraConsent -> IntroPage(
        "Connecting",
        "Tap your camera when Android asks.",
        "The first time, Android lists the cameras it can see and asks which one. After that it remembers yours " +
            "and connects without asking.",
        "Nearby devices" to "Android asks once whether Sightline may look for nearby Wi-Fi. It is used for the " +
            "camera and nothing else.",
    )

    OnboardingPage.Ready -> IntroPage(
        "Ready",
        "Wake the camera's Wi-Fi.",
        "On most cameras of this kind, hold the Wi-Fi or Up button until the screen shows a Wi-Fi name and a " +
            "password. Then connect from the Live tab.",
        null,
    )
}

/** The first-run introduction: four short pages, with back and a way to skip. */
@Composable
fun OnboardingScreen(onFinish: () -> Unit) {
    var page by rememberSaveable { mutableStateOf(OnboardingPage.Welcome) }
    val state = OnboardingState(page)
    val intro = introFor(page)
    val last = page == OnboardingPage.entries.last()

    Box(Modifier.fillMaxSize().background(RexColors.Ink), contentAlignment = Alignment.Center) {
        Column(
            Modifier.widthIn(
                max = 520.dp,
            ).fillMaxWidth().verticalScroll(rememberScrollState()).padding(RexSpace.PageMargin),
            verticalArrangement = Arrangement.spacedBy(RexSpace.Medium),
        ) {
            Dots(page)
            RexText(intro.eyebrow.uppercase(Locale.ROOT), style = RexType.LabelSmall.copy(color = RexColors.Signal))
            RexText(intro.title, style = RexType.HeadlineLarge, modifier = Modifier.semantics { heading() })
            RexText(intro.body, style = RexType.BodyMedium.copy(color = RexColors.Muted))
            intro.note?.let { (heading, body) -> AdvisoryBlock(heading, body) }
            Box(Modifier.height(RexSpace.Medium))
            Row(
                horizontalArrangement = Arrangement.spacedBy(RexSpace.Small),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (page != OnboardingPage.entries.first()) {
                    OutlineAction(text = "Back", onClick = { page = state.previous().page })
                }
                SignalButton(
                    text = if (last) "Start" else "Next",
                    onClick = { if (last) onFinish() else page = state.next().page },
                    modifier = Modifier.weight(1f),
                )
            }
            if (!last) {
                OutlineAction(text = "Skip the introduction", onClick = onFinish, modifier = Modifier.fillMaxWidth())
            }
        }
    }
}

/** Which page of how many, as dots; the current one in Signal. Decorative: the words say it too. */
@Composable
private fun Dots(current: OnboardingPage) {
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        OnboardingPage.entries.forEach { page ->
            Box(Modifier.size(8.dp).background(if (page == current) RexColors.Signal else RexColors.Line, CircleShape))
        }
    }
}
