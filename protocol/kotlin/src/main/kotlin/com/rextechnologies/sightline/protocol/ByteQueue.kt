package com.rextechnologies.sightline.protocol

/**
 * Bytes that have arrived but not yet been used: appended at the back as reads complete, and taken
 * from the front once they make something whole.
 *
 * TCP promises no boundaries, so every reader in this library keeps one of these between reads.
 */
internal class ByteQueue {
    private var bytes = ByteArray(INITIAL_CAPACITY)

    /** How many bytes are held. */
    var size: Int = 0
        private set

    /** The held bytes in place: valid up to [size], and only until the queue next changes. */
    val array: ByteArray
        get() = bytes

    /** Adds [length] bytes of [source], starting at [offset], at the back. */
    fun append(source: ByteArray, offset: Int = 0, length: Int = source.size - offset) {
        if (size + length > bytes.size) {
            bytes = bytes.copyOf(maxOf(size + length, bytes.size * 2))
        }
        source.copyInto(bytes, size, offset, offset + length)
        size += length
    }

    /** Where [pattern] first begins, or -1 when it has not arrived. */
    fun indexOf(pattern: ByteArray): Int {
        var start = 0
        while (start + pattern.size <= size) {
            if (matchesAt(start, pattern)) {
                return start
            }
            start++
        }
        return -1
    }

    /** A copy of everything held. */
    fun toByteArray(): ByteArray = bytes.copyOf(size)

    /** Drops [count] bytes from the front. */
    fun removeFirst(count: Int) {
        bytes.copyInto(bytes, 0, count, size)
        size -= count
    }

    /** Drops everything. */
    fun clear() {
        size = 0
    }

    private fun matchesAt(start: Int, pattern: ByteArray): Boolean {
        for (i in pattern.indices) {
            if (bytes[start + i] != pattern[i]) {
                return false
            }
        }
        return true
    }

    private companion object {
        const val INITIAL_CAPACITY = 256
    }
}
