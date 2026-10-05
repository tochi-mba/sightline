package com.rextechnologies.sightline.core.navigation

/**
 * Where the app is, and where back goes from there.
 *
 * An immutable stack of [Destination]s with the root at the bottom and the destination on screen at
 * the top. Every operation returns a new stack, so a screen can never observe one half-changed, and
 * a stack is never empty: there is always somewhere the app is.
 *
 * The navigation bar moves with [select]. Screens that open on top of a destination [push] and
 * [replaceTop]. Back is [pop], which leaves the app only from the root.
 */
class BackStack private constructor(
    /** The root first, then each destination above it, ending with the one on screen. */
    val entries: List<Destination>,
) {
    /** The destination on screen. */
    val current: Destination get() = entries.last()

    /** The bottom of the stack: the last place back reaches before it leaves the app. */
    val root: Destination get() = entries.first()

    /** Whether back stays in the app. False only at the root, where back leaves it. */
    val canGoBack: Boolean get() = entries.size > 1

    /** Shows [destination] on top of the current one, so back returns to where this was. */
    fun push(destination: Destination): BackStack = BackStack(entries + destination)

    /**
     * Shows [destination] in place of the current one, so back skips what it replaced.
     *
     * At the root this replaces the root itself, which is how a flow hands over to the place it led
     * to: once it has, back leaves the app rather than replaying the flow.
     */
    fun replaceTop(destination: Destination): BackStack = BackStack(entries.dropLast(1) + destination)

    /** What back does from here: shows the destination underneath or, at the root, leaves the app. */
    fun pop(): Back = if (canGoBack) Back.To(BackStack(entries.dropLast(1))) else Back.Exit

    /** Unwinds to the root in one step. */
    fun popToRoot(): BackStack = if (canGoBack) BackStack(listOf(root)) else this

    /**
     * Goes to a top-level destination, the way the navigation bar does.
     *
     * The root is reached by unwinding to it. Any other destination goes directly above the root, in
     * place of whatever was there, so back from any tab returns to the root and then leaves rather
     * than replaying every tab visited. Selecting the destination already on screen changes nothing.
     */
    fun select(destination: Destination): BackStack = when (destination) {
        current -> this
        root -> popToRoot()
        else -> popToRoot().push(destination)
    }

    /** The stack as names, which an Android saved-state bundle can hold. [restore] reads it back. */
    fun save(): List<String> = entries.map { it.name }

    override fun equals(other: Any?): Boolean = other is BackStack && other.entries == entries

    override fun hashCode(): Int = entries.hashCode()

    override fun toString(): String = entries.joinToString(prefix = "BackStack(", separator = " > ", postfix = ")")

    companion object {
        /** Where the app opens: [Destination.Start] alone. */
        val Initial: BackStack = of(Destination.Start)

        /** A stack with [root] at the bottom and [above] on top of it in order, the last on screen. */
        fun of(root: Destination, vararg above: Destination): BackStack = BackStack(listOf(root) + above)

        /**
         * Reads back what [save] wrote.
         *
         * A list it cannot read in full, empty or naming a destination this version does not have,
         * gives [Initial] rather than a guess at what was meant: opening at the start is always safe.
         */
        fun restore(names: List<String>): BackStack {
            val destinations = names.mapNotNull { name -> Destination.entries.firstOrNull { it.name == name } }
            return if (destinations.isEmpty() || destinations.size != names.size) Initial else BackStack(destinations)
        }
    }
}

/** What a back press does: moves down the stack, or leaves the app. */
sealed interface Back {
    /** Back stays in the app and shows [stack]. */
    data class To(val stack: BackStack) : Back

    /** Back at the root leaves the app, so the press belongs to the system. */
    data object Exit : Back
}
