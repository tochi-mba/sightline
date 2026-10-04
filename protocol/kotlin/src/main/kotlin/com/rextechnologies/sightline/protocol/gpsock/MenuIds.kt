package com.rextechnologies.sightline.protocol.gpsock

/**
 * The menu ids whose meaning this project knows.
 *
 * The camera's own menu ([GpSockCommand.GetParameterFile]) is the authority on which settings
 * exist, what they are called and what values they take; an app renders whatever it says. These
 * names exist for the few ids an app must treat specially — an action that erases the card, text
 * that changes how the camera is reached, the clock.
 *
 * Two ids are not in the menu at all: [SYNC_TIME] and [FIRMWARE_VERSION] are hidden ids the firmware
 * accepts but does not advertise. Kept in step with the .NET MenuIds.
 */
object MenuIds {
    /** Video resolution: 4K down to VGA on the reference camera. */
    const val RECORD_RESOLUTION = 0x0000

    /** Video exposure compensation. */
    const val RECORD_EXPOSURE = 0x0001

    /** Loop recording: off, or the length of each looped clip. */
    const val LOOP_RECORDING = 0x0003

    /** Whether the camera records its own microphone. */
    const val RECORD_AUDIO = 0x0005

    /** Whether the date and time are burned into the video. */
    const val RECORD_DATE_STAMP = 0x0006

    /** Photo resolution: 16 MP down to VGA on the reference camera. */
    const val CAPTURE_RESOLUTION = 0x0100

    /** Photo JPEG quality. */
    const val CAPTURE_QUALITY = 0x0102

    /** Burst photos. */
    const val CAPTURE_SEQUENCE = 0x0103

    /** Photo sharpening. */
    const val CAPTURE_SHARPNESS = 0x0104

    /** White balance. */
    const val CAPTURE_WHITE_BALANCE = 0x0108

    /** Mains frequency, against flicker under artificial light. */
    const val FREQUENCY = 0x0200

    /** When the camera's screen turns itself off. */
    const val SCREEN_SAVER = 0x0201

    /** When the camera turns itself off. */
    const val AUTO_POWER_OFF = 0x0202

    /** The language of the camera's own screen. */
    const val LANGUAGE = 0x0203

    /** Whether the camera beeps. */
    const val BEEP_SOUND = 0x0204

    /** How the camera writes dates: year, month or day first. */
    const val DATE_FORMAT = 0x0205

    /** An action: erase the card. Destructive and irreversible. */
    const val FORMAT_CARD = 0x0207

    /** An action: put every setting back to the factory value. */
    const val DEFAULT_SETTINGS = 0x0208

    /** Read-only: the firmware build, as the menu shows it. */
    const val VERSION = 0x0209

    /** Hidden: sets the camera's clock. Not proven on the reference camera; see docs/PROTOCOL.md. */
    const val SYNC_TIME = 0x020A

    /** Hidden: the firmware version string. */
    const val FIRMWARE_VERSION = 0x020B

    /** Text: the camera's Wi-Fi network name. */
    const val WIFI_NAME = 0x0300

    /** Text: the camera's Wi-Fi password. */
    const val WIFI_PASSWORD = 0x0301
}
