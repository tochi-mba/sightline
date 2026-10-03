package com.rextechnologies.sightline.core.onboarding

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertSame
import kotlin.test.assertTrue

class OnboardingTest {
    @Test
    fun `onboarding advances through every explanation and completes after ready`() {
        var state = OnboardingState()
        assertEquals(OnboardingPage.Welcome, state.page)
        assertFalse(state.complete)

        state = state.next()
        assertEquals(OnboardingPage.InternetStaysOn, state.page)
        state = state.next()
        assertEquals(OnboardingPage.CameraConsent, state.page)
        state = state.next()
        assertEquals(OnboardingPage.Ready, state.page)
        assertFalse(state.complete)
        state = state.next()

        assertEquals(OnboardingPage.Ready, state.page)
        assertTrue(state.complete)
    }

    @Test
    fun `back from the first page changes nothing and back elsewhere moves one page`() {
        val initial = OnboardingState()

        assertSame(initial, initial.previous())
        assertEquals(OnboardingPage.Welcome, initial.next().previous().page)
    }

    @Test
    fun `finish records completion from any page`() {
        assertEquals(OnboardingState(OnboardingPage.Ready, complete = true), OnboardingState().finish())
    }
}
