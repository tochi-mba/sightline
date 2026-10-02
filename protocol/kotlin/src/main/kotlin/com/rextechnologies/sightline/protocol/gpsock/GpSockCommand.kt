package com.rextechnologies.sightline.protocol.gpsock

/**
 * What a GPSOCKET frame is: a command from us, or the camera's answer.
 *
 * @property code The number on the wire, a little-endian 16-bit value.
 */
enum class GpSockType(val code: Int) {
    /** A request. Every frame this app sends carries this. */
    Command(1),

    /** The camera did it. The payload is the answer, if there is one. */
    Ack(2),

    /** The camera refused. The payload's first two bytes are a [NakCode]. */
    Nak(3),
    ;

    companion object {
        /** The type numbered [code], or null when GPSOCKET defines none. */
        fun fromCode(code: Int): GpSockType? = entries.firstOrNull { it.code == code }
    }
}

/**
 * The commands the camera understands.
 *
 * The numbering is the vendor's: the high byte selects a group and the low byte a command within
 * it, and the two travel as separate bytes on the wire. Every value here was read out of the
 * camera's own firmware headers and the reachable ones were confirmed against the device.
 *
 * @property code The number on the wire: the group in the high byte, the command in the low.
 */
enum class GpSockCommand(val code: Int) {
    /** Switch between record, capture and browse. Payload: one [CameraMode] byte. */
    SetMode(0x0000),

    /** Mode, battery, card and storage. See [DeviceStatus]. */
    GetDeviceStatus(0x0001),

    /** The menu XML: every setting this camera supports, with its allowed values. */
    GetParameterFile(0x0002),

    /** Turn the camera off. */
    PowerOff(0x0003),

    /**
     * Start the media flow.
     *
     * Not optional. RTSP SETUP and PLAY can both succeed and still deliver no packets until this
     * has been sent, which is the most common "connects but no picture" failure in this family.
     */
    RestartStreaming(0x0004),

    /**
     * A client-genuineness check, not a login.
     *
     * The firmware's dispatcher treats it like any other command and gates nothing behind it, so
     * Sightline does not send it. Kept here so the number is not mistaken for something else.
     */
    AuthDevice(0x0005),

    /** Start or stop recording to the camera's card. The camera toggles; it takes no argument. */
    RecordToggle(0x0100),

    /** Turn the camera's own microphone on or off for its recordings. */
    RecordAudio(0x0101),

    /** Take a photograph. */
    CapturePicture(0x0200),

    /** Begin playing a file from the card on the camera itself. */
    PlaybackStart(0x0300),

    /** Pause camera-side playback. */
    PlaybackPause(0x0301),

    /** How many files are on the card. Payload: one little-endian 16-bit count. */
    PlaybackGetFileCount(0x0302),

    /** A page of the file list. Payload: a first-page flag, then a 16-bit index. */
    PlaybackGetFileList(0x0303),

    /** A file's thumbnail. Payload: a 16-bit index. */
    PlaybackGetThumbnail(0x0304),

    /** A file's bytes, chunked. Payload: a 16-bit index. */
    PlaybackGetRawData(0x0305),

    /** Stop camera-side playback. */
    PlaybackStop(0x0306),

    /** A file's name. Payload: a 16-bit index. */
    PlaybackGetSpecificName(0x0307),

    /** Delete a file from the card. Payload: a 16-bit index. */
    PlaybackDeleteFile(0x0308),

    /** Read one setting. Payload: a 32-bit menu id. */
    MenuGetParameter(0x0400),

    /** Write one setting. Payload: a 32-bit menu id, then the value. */
    MenuSetParameter(0x0401),
    ;

    companion object {
        /** The command numbered [code], or null when this library does not know it. */
        fun fromCode(code: Int): GpSockCommand? = entries.firstOrNull { it.code == code }
    }
}

/**
 * Which mode the camera is in, which decides what it will accept.
 *
 * Browsing the card is only possible in [Browse] with streaming stopped; asking for a file list in
 * any other mode is answered with [NakCode.ServerBusy].
 *
 * @property code The number on the wire: one byte.
 */
enum class CameraMode(val code: Int) {
    /** Recording video, and the mode the camera starts in. */
    Record(0),

    /** Taking photographs. */
    Capture(1),

    /** Reading the card. Required before any playback command. */
    Browse(2),
    ;

    companion object {
        /** The mode numbered [code], or null when this library does not know it. */
        fun fromCode(code: Int): CameraMode? = entries.firstOrNull { it.code == code }
    }
}

/**
 * Why the camera refused a command.
 *
 * @property code The number on the wire: a little-endian signed 16-bit value.
 */
enum class NakCode(val code: Int) {
    /** No error. */
    Ok(0),

    /** Busy. Usually the wrong mode, or streaming still running during a browse. */
    ServerBusy(-1),

    /** The camera does not know this command. */
    InvalidCommand(-2),

    /** The camera gave up waiting. */
    RequestTimeout(-3),

    /** The command does not apply in the current mode. */
    ModeError(-4),

    /** No card. */
    NoStorage(-5),

    /** The card could not be written. */
    WriteFail(-6),

    /** The file list could not be read. */
    GetFileListFail(-7),

    /** The thumbnail could not be read. */
    GetThumbnailFail(-8),

    /** The card is full. */
    FullStorage(-9),

    /** The battery is too low to do this. */
    BatteryLow(-10),

    /** The camera ran out of memory. */
    MemoryError(-11),

    /** A checksum did not match. */
    ChecksumError(-12),

    /** Setting the clock failed. */
    SyncTimeError(-13),
    ;

    companion object {
        /** The reason numbered [code], or null when the camera gave one this library has no name for. */
        fun fromCode(code: Int): NakCode? = entries.firstOrNull { it.code == code }
    }
}
