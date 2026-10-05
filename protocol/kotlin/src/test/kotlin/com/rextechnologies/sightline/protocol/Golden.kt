package com.rextechnologies.sightline.protocol

/** The committed vectors in protocol/golden, which the .NET tests read as well. */
object Golden {
    /** The bytes of one golden file, by its path inside protocol/golden. */
    fun bytes(path: String): ByteArray {
        val stream = checkNotNull(Golden::class.java.getResourceAsStream("/$path")) {
            "protocol/golden/$path is not on the test classpath."
        }
        return stream.use { it.readBytes() }
    }

    /** One golden file as text. */
    fun text(path: String): String = bytes(path).toString(Charsets.UTF_8)
}
