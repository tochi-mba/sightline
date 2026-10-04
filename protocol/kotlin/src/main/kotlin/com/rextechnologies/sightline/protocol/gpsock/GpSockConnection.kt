package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.ByteQueue
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.readUInt16LittleEndian
import com.rextechnologies.sightline.protocol.writeInt32LittleEndian
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.io.Closeable
import java.io.OutputStream

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
 * Every answer names the command it answers; the firmware echoes it. A frame that answers some
 * other command is a leftover from a request that was abandoned — one whose caller was cancelled
 * while it waited, say — and is skipped rather than handed to whoever asked next.
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

    /** How many leftover answers to abandoned requests have been skipped. */
    var staleFramesSkipped: Int = 0
        private set

    /** Why the channel can no longer be trusted, once a cancelled transfer would not wind down. */
    private var outOfStep: String? = null

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
            throwIfOutOfStep()
            transport.send(GpSockFrame.encode(command, payload))
            readAnswer(command)
        }

    /**
     * Sends a command and gathers the chunked answer the camera streams back.
     *
     * The menu and a thumbnail arrive as a run of acknowledgements ended by an empty one. For a
     * file's bytes use [download], which does not hold the whole file in memory.
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
    ): ByteArray {
        val gathered = ByteQueue()
        streamChunks(command, payload) { chunk ->
            gathered.append(chunk)
            onProgress?.invoke(gathered.size)
        }
        return gathered.toByteArray()
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

    /** Turns the camera off. The session ends with it. */
    suspend fun powerOff() {
        demand(GpSockCommand.PowerOff)
    }

    /**
     * How many files are on the card.
     *
     * Needs [CameraMode.Browse]. The firmware answers an empty card with [NakCode.NoStorage], the
     * same refusal as no card at all, so this returns 0 for both and the caller says so in words
     * that cover either.
     *
     * @throws GpSockRefusedException The camera refused for any other reason.
     */
    suspend fun getFileCount(): Int {
        val response = ask(GpSockCommand.PlaybackGetFileCount)
        if (response.type == GpSockType.Nak) {
            if (response.nak == NakCode.NoStorage) {
                return 0
            }

            throw GpSockRefusedException(GpSockCommand.PlaybackGetFileCount, response.nakCode)
        }

        return if (response.payload.size >= 2) response.payload.readUInt16LittleEndian(0) else 0
    }

    /**
     * Every file on the card, newest page last, as the camera lists them.
     *
     * Needs [CameraMode.Browse]. The list comes in pages: the first is asked for with a flag, and
     * each later one by the index of the last file already seen. Paging stops at the count the
     * camera gave, at an empty page, or at a page that adds nothing new — a camera that kept
     * repeating itself must not keep an app here forever.
     */
    suspend fun getFileList(): List<CameraFile> {
        val count = getFileCount()
        val files = ArrayList<CameraFile>(count)
        val seen = HashSet<Int>()
        var first = true
        var lastIndex = 0
        while (files.size < count) {
            val request = byteArrayOf(if (first) 1 else 0, lastIndex.toByte(), (lastIndex shr 8).toByte())
            val response = demand(GpSockCommand.PlaybackGetFileList, request)
            var added = 0
            for (file in CameraFile.parsePage(response.payload)) {
                // The count and the pages are separate answers, and a file can be written between
                // them. The count is what was asked about, so the list is held to it.
                if (files.size < count && seen.add(file.index)) {
                    files += file
                    lastIndex = file.index
                    added++
                }
            }

            if (added == 0) {
                break
            }

            first = false
        }

        return files
    }

    /** A file's thumbnail, as a JPEG. Needs [CameraMode.Browse] with the stream stopped. */
    suspend fun getThumbnail(index: Int): ByteArray = askForChunks(GpSockCommand.PlaybackGetThumbnail, fileIndex(index))

    /** Deletes a file from the card. Needs [CameraMode.Browse]. */
    suspend fun deleteFile(index: Int) {
        demand(GpSockCommand.PlaybackDeleteFile, fileIndex(index))
    }

    /**
     * Copies a file off the card into [destination], a frame at a time.
     *
     * Needs [CameraMode.Browse]. A 4K video is gigabytes, so nothing is gathered in memory.
     * Cancelling is safe: the camera stops sending as soon as it sees another request, so a cancelled
     * download sends one, lets the camera finish what was already in flight, and leaves the channel
     * ready for the next command.
     *
     * @param index The file's index, from [getFileList].
     * @param destination Where the bytes go.
     * @param onProgress Called with the running total of bytes.
     * @return How many bytes were written.
     */
    suspend fun download(index: Int, destination: OutputStream, onProgress: ((Long) -> Unit)? = null): Long {
        var total = 0L
        streamChunks(GpSockCommand.PlaybackGetRawData, fileIndex(index)) { chunk ->
            destination.write(chunk)
            total += chunk.size
            onProgress?.invoke(total)
        }
        return total
    }

    /**
     * Reads one setting's current value, as the bytes the camera sent.
     *
     * A choice comes back as one byte, its value id; text comes back as its characters. The menu
     * says which a setting is. An id the firmware does not know is answered with a zero rather than
     * a refusal, so the menu, not this, is what says a setting exists.
     */
    suspend fun getSetting(id: Int): ByteArray {
        val request = ByteArray(4)
        request.writeInt32LittleEndian(0, id)
        return demand(GpSockCommand.MenuGetParameter, request).payload
    }

    /**
     * Reads a choice setting's current value id.
     *
     * @throws GpSockProtocolException The camera sent nothing for it.
     */
    suspend fun getChoice(id: Int): Int {
        val value = getSetting(id)
        if (value.isEmpty()) {
            throw GpSockProtocolException("The camera sent no value for setting 0x${"%04X".format(id)}.")
        }

        return value[0].toInt() and 0xFF
    }

    /** Reads a text setting, such as the camera's Wi-Fi name. */
    suspend fun getText(id: Int): String {
        val value = getSetting(id)
        val end = value.indexOf(0.toByte()).let { if (it < 0) value.size else it }
        return String(value, 0, end, Charsets.US_ASCII).trim()
    }

    /**
     * Writes a choice setting.
     *
     * @param id The menu id, from the camera's own catalogue.
     * @param value The value id to select, 0 to 255.
     */
    suspend fun setSetting(id: Int, value: Int) {
        require(value in 0..0xFF) { "A choice is one byte, 0 to 255; $value is not." }
        // Layout from the firmware: a 32-bit little-endian id, a size byte, then the value.
        demand(GpSockCommand.MenuSetParameter, setPayload(id, byteArrayOf(value.toByte())))
    }

    /**
     * Writes a text setting into the camera's fixed-size field.
     *
     * The firmware copies exactly [fieldLength] bytes, whatever was sent, so the text is padded with
     * zeros to that length; sending less would let it copy whatever happened to follow in its buffer.
     * The length is the camera's, learned by reading the field first.
     *
     * @param id The menu id.
     * @param value Printable ASCII, no longer than the field.
     * @param fieldLength The size of the camera's field for this setting, 1 to 255.
     */
    suspend fun setText(id: Int, value: String, fieldLength: Int) {
        require(fieldLength in 1..0xFF) { "A text field is 1 to 255 bytes; $fieldLength is not." }
        require(value.isNotEmpty() && value.length <= fieldLength && value.all { it in ' '..'~' }) {
            "The camera takes 1 to $fieldLength printable characters here."
        }

        val field = ByteArray(fieldLength)
        value.toByteArray(Charsets.US_ASCII).copyInto(field)
        demand(GpSockCommand.MenuSetParameter, setPayload(id, field))
    }

    /**
     * Sends a command whose answer is a run of chunks, handing each to [onChunk] as it arrives.
     *
     * Until the camera's last word on this command has been read, it may still be sending. Leaving
     * early for any reason — cancelled, or [onChunk] failing to take a chunk — must stop it first, or
     * its remaining frames become the answer to the next request. That clean-up runs even in a
     * cancelled coroutine, which otherwise could not talk to the camera on its way out.
     */
    private suspend fun streamChunks(command: GpSockCommand, payload: ByteArray, onChunk: suspend (ByteArray) -> Unit) {
        oneAtATime.withLock {
            throwIfOutOfStep()
            transport.send(GpSockFrame.encode(command, payload))
            var finished = false
            try {
                while (!finished) {
                    val response = readAnswer(command)
                    if (response.type == GpSockType.Nak) {
                        finished = true
                        throw GpSockRefusedException(command, response.nakCode)
                    }

                    finished = response.isEndOfChunks
                    if (!finished) {
                        onChunk(response.payload)
                    }
                }
            } catch (failure: Throwable) {
                // Only a way out before the camera's last word needs stopping; a refusal is its last word.
                if (!finished) {
                    withContext(NonCancellable) { windDown(command) }
                }

                throw failure
            }
        }
    }

    /**
     * Stops a transfer the camera is still sending, and reads past what it already sent.
     *
     * The firmware checks for a new request between frames and abandons the transfer when it sees
     * one. The request that stopped it is used up in doing so — the reference camera answered the
     * transfer with a refusal and never answered the request (2026-10-02). If the transfer's last
     * word is instead the normal end, it had finished before the request arrived, so the camera
     * answers the request like any other, and that answer is read too. If the camera does not wind
     * down in time the channel is marked out of step, and every later request fails at once rather
     * than reading part of a file as its answer.
     */
    private suspend fun windDown(transfer: GpSockCommand) {
        val outcome = runCatching {
            withTimeout(WIND_DOWN_TIMEOUT_MILLIS) {
                transport.send(GpSockFrame.encode(GpSockCommand.GetDeviceStatus))
                var response: GpSockResponse
                do {
                    response = readAnswer(transfer)
                } while (response.type != GpSockType.Nak && !response.isEndOfChunks)

                if (response.isEndOfChunks) {
                    readAnswer(GpSockCommand.GetDeviceStatus)
                }
            }
        }
        outcome.exceptionOrNull()?.let { failure ->
            outOfStep = "A cancelled $transfer did not wind down (${failure.message})."
        }
    }

    private fun throwIfOutOfStep() {
        outOfStep?.let { reason ->
            throw GpSockProtocolException("The control channel is out of step and must be reopened. $reason")
        }
    }

    private fun setPayload(id: Int, value: ByteArray): ByteArray {
        val payload = ByteArray(5 + value.size)
        payload.writeInt32LittleEndian(0, id)
        payload[4] = value.size.toByte()
        value.copyInto(payload, 5)
        return payload
    }

    /** Reads frames until one answers [expected], skipping and counting any left over from before. */
    private suspend fun readAnswer(expected: GpSockCommand): GpSockResponse {
        while (true) {
            val response = readFrame()
            if (response.command == expected) {
                return response
            }

            staleFramesSkipped++
        }
    }

    /** A file's index as every playback command takes it: 16 bits, little-endian. */
    private fun fileIndex(index: Int): ByteArray {
        require(index in 0..0xFFFF) { "The camera numbers its files from 0 to 65535, so there is no file $index." }
        return byteArrayOf(index.toByte(), (index shr 8).toByte())
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

        /** How long a cancelled transfer may take to wind down before the channel is given up on. */
        const val WIND_DOWN_TIMEOUT_MILLIS = 5_000L
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
            NakCode.NoStorage -> "there is no memory card in it, or the card is empty"
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
