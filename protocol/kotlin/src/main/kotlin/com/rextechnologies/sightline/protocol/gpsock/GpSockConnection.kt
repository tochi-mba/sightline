package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.ByteQueue
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.readUInt16LittleEndian
import com.rextechnologies.sightline.protocol.writeInt32LittleEndian
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.Closeable

/**
 * The camera's control channel: one connection, held open for the whole session.
 *
 * **This connection must not be opened per command.** The camera's firmware treats the socket
 * as the client's liveness signal: when it closes, the camera stops recording, tears down the
 * stream and aborts a burst capture. A client that connected, sent a shutter command and
 * disconnected would therefore take the photograph and immediately stop whatever else the camera
 * was doing. Holding it open is also what keeps the camera's access point from going to sleep.
 *
 * The camera is single-client, so a second connection — from this app or from a vendor app — is
 * refused or starves. That is the "camera busy" case the UI explains.
 *
 * @param transport A transport that is already connected, or about to be.
 */
class GpSockConnection(private val transport: CameraTransport) : Closeable {
    private val receiveBuffer = ByteArray(8192)
    private val pending = ByteQueue()

    /**
     * One request in flight at a time.
     *
     * An app sends a shutter press while the live view is still starting the stream, and both go down
     * this one socket. Without this, the two requests interleave and each reads the other's answer —
     * the camera did both things and the app reports the wrong outcome for each.
     */
    private val oneAtATime = Mutex()

    /** Opens the control channel. */
    suspend fun open() {
        transport.connect()
    }

    /**
     * Sends a command and waits for the camera's answer.
     *
     * @param command What to ask for.
     * @param payload Its argument, empty for most commands.
     * @return The camera's answer, which may be a refusal.
     */
    suspend fun ask(command: GpSockCommand, payload: ByteArray = ByteArray(0)): GpSockResponse =
        oneAtATime.withLock {
            transport.send(GpSockFrame.encode(command, payload))
            readFrame()
        }

    /**
     * Sends a command and gathers the chunked answer the camera streams back.
     *
     * The menu, a thumbnail and a file's bytes all arrive as a run of acknowledgements of at most
     * [MAX_CHUNK_PAYLOAD] bytes, ended by an empty one.
     *
     * @param command What to ask for.
     * @param payload Its argument.
     * @param onProgress Called with the running total, for a progress bar.
     * @throws GpSockRefusedException The camera refused part-way through.
     */
    suspend fun askForChunks(
        command: GpSockCommand,
        payload: ByteArray = ByteArray(0),
        onProgress: ((Int) -> Unit)? = null,
    ): ByteArray = oneAtATime.withLock {
        transport.send(GpSockFrame.encode(command, payload))
        gatherChunks(command, onProgress)
    }

    /**
     * Sends a command and throws unless the camera accepted it.
     *
     * @throws GpSockRefusedException The camera refused.
     */
    suspend fun demand(command: GpSockCommand, payload: ByteArray = ByteArray(0)): GpSockResponse {
        val response = ask(command, payload)
        return if (response.isAck) response else throw GpSockRefusedException(command, response.nakCode)
    }

    /** Reads the camera's state. */
    suspend fun getStatus(): DeviceStatus = DeviceStatus(demand(GpSockCommand.GetDeviceStatus).payload)

    /** Reads the camera's own settings catalogue. */
    suspend fun getMenu(): MenuCatalog = MenuCatalog.parse(askForChunks(GpSockCommand.GetParameterFile))

    /** Switches the camera between recording, taking photographs and browsing the card. */
    suspend fun setMode(mode: CameraMode) {
        demand(GpSockCommand.SetMode, byteArrayOf(mode.code.toByte()))
    }

    /**
     * Starts the media flow.
     *
     * Must be sent for the stream to carry anything. RTSP SETUP and PLAY can both answer 200 and
     * still deliver nothing until this has gone.
     */
    suspend fun startStreaming() {
        demand(GpSockCommand.RestartStreaming)
    }

    /** Takes a photograph. */
    suspend fun capturePicture() {
        demand(GpSockCommand.CapturePicture)
    }

    /** Starts or stops recording to the camera's card. */
    suspend fun toggleRecording() {
        demand(GpSockCommand.RecordToggle)
    }

    /** How many files are on the card. */
    suspend fun getFileCount(): Int {
        val response = demand(GpSockCommand.PlaybackGetFileCount)
        return if (response.payload.size >= 2) response.payload.readUInt16LittleEndian(0) else 0
    }

    /**
     * Writes one setting.
     *
     * @param id The menu id, from the camera's own catalogue.
     * @param value The value to write.
     */
    suspend fun setSetting(id: Int, value: Int) {
        // Layout from the firmware: a 32-bit little-endian id, a size byte, then the value.
        val payload = ByteArray(6)
        payload.writeInt32LittleEndian(0, id)
        payload[4] = 1
        payload[5] = value.toByte()
        demand(GpSockCommand.MenuSetParameter, payload)
    }

    /** Reads the run of chunks answering [command] that was just sent, up to the empty one that ends it. */
    private suspend fun gatherChunks(command: GpSockCommand, onProgress: ((Int) -> Unit)?): ByteArray {
        val gathered = ByteQueue()
        while (true) {
            val response = readFrame()
            if (response.type == GpSockType.Nak) {
                throw GpSockRefusedException(command, response.nakCode)
            }

            if (response.isEndOfChunks) {
                return gathered.toByteArray()
            }

            gathered.append(response.payload)
            onProgress?.invoke(gathered.size)
        }
    }

    private suspend fun readFrame(): GpSockResponse {
        while (true) {
            val decoded = GpSockFrame.tryDecode(pending.array, 0, pending.size)
            if (decoded != null) {
                pending.removeFirst(decoded.consumed)
                return decoded.response
            }

            val read = transport.receive(receiveBuffer)
            if (read == 0) {
                throw GpSockProtocolException("The camera closed the control channel.")
            }

            pending.append(receiveBuffer, 0, read)
        }
    }

    /**
     * Closes the control channel.
     *
     * This is not free: the camera stops recording and tears down the stream when it happens. It
     * belongs at the end of a session and nowhere else.
     */
    override fun close() {
        transport.close()
    }

    companion object {
        /** The port the control server listens on. */
        const val PORT = 8081

        /**
         * The camera's own buffer size, which is why long answers arrive in pieces.
         *
         * The firmware's buffer is 256 bytes, of which the header takes 14, so a chunk carries at
         * most 242. A reader that assumed one answer per frame would silently truncate the menu.
         */
        const val MAX_CHUNK_PAYLOAD = 242
    }
}

/**
 * The camera refused a command, and said why.
 *
 * @property command What was asked for.
 * @property code Why the camera would not, exactly as it said it: a [NakCode] number, or one this
 *   library has no name for.
 */
class GpSockRefusedException(
    val command: GpSockCommand,
    val code: Int,
) : Exception("The camera refused $command: ${explain(code)}.") {
    /** Why the camera would not, or null when it gave a reason this library has no name for; see [code]. */
    val reason: NakCode?
        get() = NakCode.fromCode(code)

    companion object {
        /** The refusal in words a person can act on. */
        fun explain(reason: NakCode): String = when (reason) {
            NakCode.Ok -> "no error"
            NakCode.ServerBusy -> "it is busy, which usually means it is in the wrong mode or still streaming"
            NakCode.InvalidCommand -> "it does not know that command"
            NakCode.RequestTimeout -> "it gave up waiting"
            NakCode.ModeError -> "that cannot be done in the mode it is in"
            NakCode.NoStorage -> "there is no memory card in it"
            NakCode.WriteFail -> "the card could not be written"
            NakCode.GetFileListFail -> "it could not read the file list"
            NakCode.GetThumbnailFail -> "it could not read the thumbnail"
            NakCode.FullStorage -> "the card is full"
            NakCode.BatteryLow -> "its battery is too low"
            NakCode.MemoryError -> "it ran out of memory"
            NakCode.ChecksumError -> "a checksum did not match"
            NakCode.SyncTimeError -> "it would not take the clock"
        }

        /** The refusal the camera sent as [code] in words, or by number when it has no name. */
        fun explain(code: Int): String {
            val reason = NakCode.fromCode(code)
            return if (reason != null) explain(reason) else "reason $code"
        }
    }
}
