package com.rextechnologies.sightline.core.onboarding

/** The first-run pages, in the order a person sees them. */
enum class OnboardingPage {
    Welcome,
    InternetStaysOn,
    CameraConsent,
    Ready,
}

/**
 * Persistent first-run progress. The system Wi-Fi consent dialog is deliberately not represented
 * as a setting: Android owns that consent and the app asks for it again only when connecting.
 */
data class OnboardingState(
    val page: OnboardingPage = OnboardingPage.Welcome,
    val complete: Boolean = false,
) {
    fun next(): OnboardingState {
        val following = OnboardingPage.entries.getOrNull(page.ordinal + 1)
        return if (following == null) copy(complete = true) else copy(page = following)
    }

    fun previous(): OnboardingState {
        val prior = OnboardingPage.entries.getOrNull(page.ordinal - 1)
        return if (prior == null) this else copy(page = prior)
    }

    fun finish(): OnboardingState = copy(page = OnboardingPage.Ready, complete = true)
}
