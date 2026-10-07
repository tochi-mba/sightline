package com.rextechnologies.sightline.protocol

/**
 * The zeros the reference camera pads every JPEG with, in its stream and in its clips alike: up to seven after
 * the end-of-image marker, to a multiple of eight bytes.
 */
object JpegPadding {
    /**
     * How long the first [length] bytes of [picture] are without the zeros that follow its end-of-image marker.
     * Zeros with no marker before them are counted in: they could be the picture's own, and a picture with no
     * end is judged on its own.
     */
    fun end(picture: ByteArray, length: Int): Int {
        var end = length
        while (end > 0 && picture[end - 1].toInt() == 0) {
            end--
        }

        return if (end >= 2 && picture.unsignedAt(end - 2) == 0xFF &&
            picture.unsignedAt(end - 1) == 0xD9
        ) {
            end
        } else {
            length
        }
    }
}
