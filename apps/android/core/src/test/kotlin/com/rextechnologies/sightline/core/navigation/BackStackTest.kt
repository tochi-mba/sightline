package com.rextechnologies.sightline.core.navigation

import com.rextechnologies.sightline.core.navigation.Destination.Library
import com.rextechnologies.sightline.core.navigation.Destination.Live
import com.rextechnologies.sightline.core.navigation.Destination.Sentry
import com.rextechnologies.sightline.core.navigation.Destination.Settings
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertSame
import kotlin.test.assertTrue

class BackStackTest {
    @Test
    fun `the navigation lists the four destinations in order and the app starts on Live`() {
        assertEquals(listOf(Live, Library, Sentry, Settings), Destination.entries)
        assertEquals(Live, Destination.Start)
    }

    @Test
    fun `the app opens on the start destination alone, where back leaves the app`() {
        val stack = BackStack.Initial

        assertEquals(listOf(Live), stack.entries)
        assertEquals(Live, stack.current)
        assertEquals(Live, stack.root)
        assertFalse(stack.canGoBack)
        assertEquals(Back.Exit, stack.pop())
    }

    @Test
    fun `push shows the new destination and back returns to the one before it`() {
        val stack = BackStack.Initial.push(Library).push(Settings)

        assertEquals(listOf(Live, Library, Settings), stack.entries)
        assertEquals(Settings, stack.current)
        assertEquals(Live, stack.root)
        assertTrue(stack.canGoBack)
        assertEquals(Back.To(BackStack.of(Live, Library)), stack.pop())
    }

    @Test
    fun `back walks down one destination at a time and leaves the app only from the root`() {
        var stack = BackStack.of(Live, Library, Sentry)
        val shown = mutableListOf(stack.current)
        while (true) {
            val back = stack.pop()
            if (back !is Back.To) break
            stack = back.stack
            shown += stack.current
        }

        assertEquals(listOf(Sentry, Library, Live), shown)
        assertEquals(Back.Exit, stack.pop())
    }

    @Test
    fun `replaceTop swaps the destination on screen so back skips what it replaced`() {
        val stack = BackStack.of(Live, Library).replaceTop(Sentry)

        assertEquals(listOf(Live, Sentry), stack.entries)
        assertEquals(Back.To(BackStack.Initial), stack.pop())
    }

    @Test
    fun `replacing the only destination replaces the root, so back then leaves the app`() {
        val stack = BackStack.Initial.replaceTop(Settings)

        assertEquals(listOf(Settings), stack.entries)
        assertEquals(Settings, stack.root)
        assertEquals(Back.Exit, stack.pop())
    }

    @Test
    fun `popToRoot unwinds everything above the root in one step`() {
        assertEquals(BackStack.Initial, BackStack.of(Live, Library, Sentry, Settings).popToRoot())
    }

    @Test
    fun `popToRoot at the root is the same stack`() {
        val stack = BackStack.Initial

        assertSame(stack, stack.popToRoot())
    }

    @Test
    fun `selecting a tab puts it directly above the root, in place of the tab that was there`() {
        val library = BackStack.Initial.select(Library)
        val settings = library.select(Settings)

        assertEquals(BackStack.of(Live, Library), library)
        assertEquals(BackStack.of(Live, Settings), settings)
        assertEquals(Back.To(BackStack.Initial), settings.pop())
    }

    @Test
    fun `selecting a tab from a screen opened on top of another tab drops that screen`() {
        assertEquals(BackStack.of(Live, Sentry), BackStack.of(Live, Library, Settings).select(Sentry))
    }

    @Test
    fun `selecting the root unwinds to it`() {
        assertEquals(BackStack.Initial, BackStack.of(Live, Library, Settings).select(Live))
    }

    @Test
    fun `selecting the destination already on screen changes nothing`() {
        val stack = BackStack.of(Live, Library)

        assertSame(stack, stack.select(Library))
        assertSame(BackStack.Initial, BackStack.Initial.select(Live))
    }

    @Test
    fun `a stack survives being saved as names and restored`() {
        val stack = BackStack.of(Live, Library, Settings)

        assertEquals(listOf("Live", "Library", "Settings"), stack.save())
        assertEquals(stack, BackStack.restore(stack.save()))
    }

    @Test
    fun `a saved stack that cannot be read in full opens at the start rather than at a guess`() {
        assertEquals(BackStack.Initial, BackStack.restore(emptyList()))
        assertEquals(BackStack.Initial, BackStack.restore(listOf("Gallery")))
        assertEquals(BackStack.Initial, BackStack.restore(listOf("Live", "Hud", "Settings")))
    }

    @Test
    fun `stacks are equal when their destinations are, and say what they hold`() {
        val stack = BackStack.of(Live, Library)

        assertEquals(BackStack.Initial.push(Library), stack)
        assertEquals(BackStack.Initial.push(Library).hashCode(), stack.hashCode())
        assertNotEquals(BackStack.of(Live, Sentry), stack)
        assertFalse(stack.equals(listOf(Live, Library)))
        assertEquals("BackStack(Live > Library)", stack.toString())
    }

    @Test
    fun `back to a stack carries that stack`() {
        val back = Back.To(BackStack.Initial)

        assertEquals(BackStack.Initial, back.stack)
        assertEquals(Back.To(BackStack.of(Live)), back)
        assertEquals("Exit", Back.Exit.toString())
    }
}
