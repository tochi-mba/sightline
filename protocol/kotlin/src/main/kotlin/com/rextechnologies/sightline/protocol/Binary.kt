package com.rextechnologies.sightline.protocol

// The fixed-width reads and writes the two wire formats need. GPSOCKET is little-endian and RTP is
// big-endian, so every function names its byte order rather than leaving it to be remembered.

/** The byte at [index], as the unsigned value the wire means by it. */
internal fun ByteArray.unsignedAt(index: Int): Int = this[index].toInt() and 0xFF

/** A little-endian unsigned 16-bit value starting at [offset]. */
internal fun ByteArray.readUInt16LittleEndian(offset: Int): Int = unsignedAt(offset) or (unsignedAt(offset + 1) shl 8)

/** A little-endian signed 16-bit value starting at [offset]. */
internal fun ByteArray.readInt16LittleEndian(offset: Int): Int = readUInt16LittleEndian(offset).toShort().toInt()

/** Writes the low 16 bits of [value] little-endian, starting at [offset]. */
internal fun ByteArray.writeUInt16LittleEndian(offset: Int, value: Int) {
    this[offset] = value.toByte()
    this[offset + 1] = (value shr 8).toByte()
}

/** Writes [value] as a little-endian 32-bit value starting at [offset]. */
internal fun ByteArray.writeInt32LittleEndian(offset: Int, value: Int) {
    this[offset] = value.toByte()
    this[offset + 1] = (value shr 8).toByte()
    this[offset + 2] = (value shr 16).toByte()
    this[offset + 3] = (value shr 24).toByte()
}

/** A big-endian unsigned 16-bit value starting at [offset]. */
internal fun ByteArray.readUInt16BigEndian(offset: Int): Int = (unsignedAt(offset) shl 8) or unsignedAt(offset + 1)

/** A big-endian 32-bit value starting at [offset], as the bits it carries. */
internal fun ByteArray.readInt32BigEndian(offset: Int): Int =
    (readUInt16BigEndian(offset) shl 16) or readUInt16BigEndian(offset + 2)
