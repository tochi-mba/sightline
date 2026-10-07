package com.rextechnologies.sightline.protocol

import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockCommand
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.gpsock.GpSockFrame
import com.rextechnologies.sightline.protocol.gpsock.GpSockType
import com.rextechnologies.sightline.protocol.gpsock.MenuIds
import com.rextechnologies.sightline.protocol.gpsock.NakCode
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.delay
import kotlinx.coroutines.yield
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.LocalDateTime
import kotlin.time.Duration

/**
 * A camera that needs no hardware, speaking the real wire format.
 *
 * It answers the way the reference camera and its firmware source do, including the behaviours
 * that are easy to get wrong: long answers arrive in chunks ended by an empty one; browsing is
 * refused unless the camera was put in browse mode first, and thumbnails also while it streams; an
 * empty card is refused as "no storage"; a file's bytes are produced only as fast as they are
 * read, and any new request abandons the transfer — consuming that request without answering it, as
 * the reference camera did on 2026-10-02; text settings live in fixed-size fields. Frames can be
 * delivered in awkward pieces so the reader is exercised the way TCP really exercises it.
 */
class FakeCamera : CameraTransport {
    private val outbox = ArrayDeque<ByteArray>()
    private val inbox = mutableListOf<Byte>()
    private val card = mutableListOf<FakeFile>()
    private var download: ByteArray? = null
    private var downloadOffset = 0

    /** How many bytes at a time this camera will hand over; 0 means all of them. */
    var dribbleBytes = 0

    /**
     * When set, every read lets other coroutines run before it hands anything over, as a read from a
     * real socket waits on the network. With [dribbleBytes], this is what lets two requests sharing
     * the connection interleave the way they would on a real link.
     */
    var answersSlowly = false

    /**
     * When set, the camera accepts commands and never answers, as one that has gone does.
     *
     * This is what the access point going to sleep mid-session looks like to the client: the
     * socket is still there, the write succeeds, and nothing ever comes back.
     */
    var hangsUp = false

    /**
     * When set, the camera takes commands and answers none of them, but keeps the connection open: a
     * read waits until its caller gives up, rather than finding the connection closed.
     *
     * This is the half-asleep camera the reference unit became after its access point slept: the port
     * still accepts, and nothing ever answers. Only a deadline gets a client out.
     */
    var goesQuiet = false

    /** How long the camera takes over each answer it sends, as a busy one does. */
    var answerDelay: Duration = Duration.ZERO

    /** When set, a download stops sending after this many bytes and the camera goes quiet mid-transfer. */
    var downloadStallsAfterBytes: Int? = null

    /** Which mode the camera is in. Browsing is refused in any other. Set it to start a test elsewhere. */
    var mode = CameraMode.Record

    /** Whether the camera is recording to its card; set it as the camera's own button would. */
    var isRecording = false

    /** The thumbnail sent for a file: a token by default, which only looks like a JPEG to the protocol. */
    var thumbnailOf: (FakeFile) -> ByteArray = { bytes(0xFF, 0xD8, it.index, 0xFF, 0xD9) }

    /** Whether the media flow has been started. */
    var isStreaming = false

    /** Told when the camera is put into browse mode, which ends its stream. */
    var enteredBrowse: () -> Unit = {}

    /** Whether it has been told to turn off. */
    var isPoweredOff = false
        private set

    /** How many photographs have been taken. */
    var picturesTaken = 0
        private set

    /** What the menu command returns. */
    var menuXml =
        """
        <Menu version="1.0"><Categories><Category><Name>Record</Name><Settings>
          <Setting><Name>Resolution</Name><ID>0x0000000</ID><Type>0x00</Type><Default>0x00</Default>
            <Values><Value><ID>0x00</ID><Name>4K</Name></Value>
                    <Value><ID>0x01</ID><Name>1080P</Name></Value></Values></Setting>
        </Settings></Category></Categories></Menu>
        """.trimIndent()

    /** The choice settings this camera has been told to change, as id and value. */
    val settingsWritten = mutableListOf<Pair<Int, Int>>()

    /** Every setting write exactly as it arrived: the id and the bytes after the size byte. */
    val rawSettingsWritten = mutableListOf<Pair<Int, ByteArray>>()

    /** What each setting reads back as. An id with no entry reads as a single zero, as the firmware does. */
    val values = mutableMapOf(
        MenuIds.WIFI_NAME to "ActionCam_000000000000".toByteArray(Charsets.US_ASCII),
        MenuIds.WIFI_PASSWORD to paddedField("12345678", WIFI_FIELD_LENGTH),
    )

    /** How many bytes of a file go in each frame of a download. */
    var downloadChunk = 1000

    /** When set, a download is answered with this refusal instead of the file. */
    var refuseDownloadWith: NakCode? = null

    /** When set, every thumbnail is answered with this refusal instead of the picture. */
    var refuseThumbnailsWith: NakCode? = null

    /** How many files to put on one page of the file list. */
    var pageSize = 4

    /** When set, every page of the file list is the first page, as a broken camera might send. */
    var repeatsFirstPage = false

    /** When false, deleting is refused the way a firmware built without it refuses. */
    var supportsDelete = true

    /** The status payload it reports; 16 bytes, as the reference camera sends. */
    var status: ByteArray = "000280010025b3000000fe7c0000a501".hexToByteArray()

    /**
     * Acknowledgements to give in place of the usual answer, by command, for the answers a firmware
     * might give that the reference camera has not been seen to.
     */
    val forcedAnswers = mutableMapOf<GpSockCommand, ByteArray>()

    /** Commands to refuse, and why, in place of the usual answer. */
    val forcedRefusals = mutableMapOf<GpSockCommand, NakCode>()

    /** Settings that are acknowledged when written but keep their value, as a firmware ignoring them does. */
    val ignoredSettings = mutableSetOf<Int>()

    /** Frames to send before the answer to the next command, as leftovers from earlier requests. */
    val strayFramesBeforeNextAnswer = mutableListOf<ByteArray>()

    /** The files on the card. */
    val files: List<FakeFile>
        get() = card

    override var isConnected = false
        private set

    /** Whether anything closed the connection, which a real camera reacts badly to. */
    var wasDisposed = false
        private set

    /** Puts a file on the card, numbered after the last one. */
    fun addFile(code: Char, taken: LocalDateTime, content: ByteArray): FakeFile {
        val file = FakeFile(code, if (card.isEmpty()) 1 else card.last().index + 1, taken, content)
        card += file
        return file
    }

    override suspend fun connect() {
        isConnected = true
    }

    override suspend fun send(bytes: ByteArray) {
        inbox += bytes.asList()
        while (inbox.size >= GpSockFrame.REQUEST_HEADER_LENGTH) {
            val frame = inbox.toByteArray()
            if (!frame.copyOfRange(0, 8).contentEquals(GpSockFrame.tag)) {
                // The real firmware answers nothing at all, which is the whole reason the port
                // looks dead to a scanner.
                inbox.clear()
                return
            }

            val command = ((frame[10].toInt() and 0xFF) shl 8) or (frame[11].toInt() and 0xFF)
            val payload = frame.copyOfRange(GpSockFrame.REQUEST_HEADER_LENGTH, frame.size)
            inbox.clear()
            if (hangsUp || goesQuiet) {
                continue
            }

            if (download != null) {
                // The firmware looks for a new request between frames and gives the transfer up, and
                // the request that stopped it is used up doing so: only the transfer's refusal comes back.
                download = null
                nak(GpSockCommand.PlaybackGetRawData.code, NakCode.InvalidCommand)
                continue
            }

            outbox.addAll(strayFramesBeforeNextAnswer)
            strayFramesBeforeNextAnswer.clear()
            handle(command, payload)
        }
    }

    override suspend fun receive(into: ByteArray): Int {
        if (answersSlowly) {
            yield()
        }

        if (answerDelay.isPositive()) {
            delay(answerDelay)
        }

        val transfer = download
        val stallAt = downloadStallsAfterBytes
        if (outbox.isEmpty() && transfer != null && stallAt != null && downloadOffset >= stallAt) {
            awaitCancellation()
        }

        if (outbox.isEmpty() && transfer != null) {
            // The next frame of a file is made only when the last one has been taken, which is what
            // gives a cancel something to interrupt.
            val size = minOf(downloadChunk, transfer.size - downloadOffset)
            ack(GpSockCommand.PlaybackGetRawData.code, transfer.copyOfRange(downloadOffset, downloadOffset + size))
            downloadOffset += size
            if (downloadOffset == transfer.size) {
                // The firmware sends the closing frame straight after the last chunk, so by the time a
                // client has handled that chunk the transfer is already over.
                download = null
                ack(GpSockCommand.PlaybackGetRawData.code)
            }
        }

        if (outbox.isEmpty() && goesQuiet) {
            awaitCancellation()
        }

        val next = outbox.removeFirstOrNull() ?: return 0
        var take = if (dribbleBytes > 0) minOf(dribbleBytes, next.size) else next.size
        take = minOf(take, into.size)
        next.copyInto(into, 0, 0, take)
        if (take < next.size) {
            // Keep order: the remainder must come before anything queued after it.
            outbox.addFirst(next.copyOfRange(take, next.size))
        }

        return take
    }

    override fun close() {
        wasDisposed = true
        isConnected = false
        // What the real camera does when the control socket goes: it stops.
        isRecording = false
        isStreaming = false
        download = null
    }

    private fun handle(command: Int, payload: ByteArray) {
        val known = GpSockCommand.fromCode(command)
        val refusal = known?.let { forcedRefusals[it] }
        if (refusal != null) {
            nak(command, refusal)
            return
        }

        val forced = known?.let { forcedAnswers[it] }
        if (forced != null) {
            ack(command, forced)
            return
        }

        if (isBusyFor(known)) {
            nak(command, NakCode.ServerBusy)
            return
        }

        when (known) {
            GpSockCommand.GetDeviceStatus -> {
                val status = status.copyOf()
                status[0] = mode.code.toByte()
                status[1] = ((if (isRecording) 1 else 0) or 0b10).toByte()
                ack(command, status)
            }

            GpSockCommand.GetParameterFile -> chunked(command, menuXml.toByteArray(Charsets.UTF_8))

            GpSockCommand.SetMode -> {
                mode = CameraMode.entries.first { it.code == payload[0].toInt() }
                if (mode == CameraMode.Browse) {
                    // The real camera stops its stream to browse, and hangs up the connection it was on.
                    isStreaming = false
                    enteredBrowse()
                }

                ack(command)
            }

            GpSockCommand.PowerOff -> {
                isPoweredOff = true
                ack(command)
            }

            GpSockCommand.RestartStreaming -> {
                isStreaming = true
                ack(command)
            }

            GpSockCommand.CapturePicture -> {
                picturesTaken++
                ack(command)
            }

            GpSockCommand.RecordToggle -> {
                isRecording = !isRecording
                ack(command)
            }

            GpSockCommand.PlaybackGetFileCount ->
                if (card.isEmpty()) {
                    // The firmware's answer to an empty card is the same as to no card.
                    nak(command, NakCode.NoStorage)
                } else {
                    ack(command, bytes(card.size and 0xFF, card.size shr 8))
                }

            GpSockCommand.PlaybackGetFileList -> ack(command, page(payload))

            GpSockCommand.PlaybackGetThumbnail -> {
                val pictured = find(payload)
                val refused = refuseThumbnailsWith
                if (refused != null) {
                    nak(command, refused)
                } else if (pictured == null) {
                    nak(command, NakCode.InvalidCommand)
                } else {
                    ack(command, thumbnailOf(pictured))
                    ack(command)
                }
            }

            GpSockCommand.PlaybackDeleteFile -> {
                val doomed = find(payload)
                if (!supportsDelete || doomed == null) {
                    // A firmware built without delete answers 0xFFFF, which reads as "busy".
                    nak(command, NakCode.ServerBusy)
                } else {
                    card.remove(doomed)
                    ack(command)
                }
            }

            GpSockCommand.PlaybackGetRawData -> {
                val refusal = refuseDownloadWith
                val wanted = find(payload)
                when {
                    refusal != null -> nak(command, refusal)
                    wanted == null -> nak(command, NakCode.InvalidCommand)
                    else -> {
                        download = wanted.content
                        downloadOffset = 0
                    }
                }
            }

            GpSockCommand.MenuGetParameter -> {
                val id = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN).int
                ack(command, values[id] ?: bytes(0))
            }

            GpSockCommand.MenuSetParameter -> {
                val id = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN).int
                val written = payload.copyOfRange(5, 5 + (payload[4].toInt() and 0xFF))
                rawSettingsWritten += id to written
                if (written.size == 1) {
                    settingsWritten += id to (written[0].toInt() and 0xFF)
                }

                if (id !in ignoredSettings) {
                    values[id] = written
                }

                ack(command)
            }

            else -> nak(command, NakCode.InvalidCommand)
        }
    }

    /** Whether the firmware refuses [command] as busy in the mode the camera is in. */
    private fun isBusyFor(command: GpSockCommand?): Boolean = when (command) {
        GpSockCommand.PlaybackGetFileCount,
        GpSockCommand.PlaybackGetFileList,
        GpSockCommand.PlaybackGetRawData,
        GpSockCommand.PlaybackDeleteFile,
        -> mode != CameraMode.Browse

        GpSockCommand.PlaybackGetThumbnail -> mode != CameraMode.Browse || isStreaming

        else -> false
    }

    /** A page of the file list: the files after the one the request names, or from the start. */
    private fun page(payload: ByteArray): ByteArray {
        val after = if (payload[0].toInt() == 1 || repeatsFirstPage) 0 else payload.uint16At(1)
        val page = card.filter { it.index > after }.take(pageSize)
        val bytes = ByteArray(1 + page.size * CameraFile.MINIMUM_ENTRY_LENGTH)
        bytes[0] = page.size.toByte()
        page.forEachIndexed { i, file ->
            val at = 1 + i * CameraFile.MINIMUM_ENTRY_LENGTH
            val kilobytes = (file.content.size + 1023) / 1024
            bytes[at] = file.code.code.toByte()
            bytes.putUInt16(at + 1, file.index)
            bytes[at + 3] = (file.taken.year - 2000).toByte()
            bytes[at + 4] = file.taken.monthValue.toByte()
            bytes[at + 5] = file.taken.dayOfMonth.toByte()
            bytes[at + 6] = file.taken.hour.toByte()
            bytes[at + 7] = file.taken.minute.toByte()
            bytes[at + 8] = file.taken.second.toByte()
            bytes.putInt32(at + 9, kilobytes)
        }

        return bytes
    }

    private fun find(payload: ByteArray): FakeFile? {
        val index = payload.uint16At(0)
        return card.firstOrNull { it.index == index }
    }

    private fun ack(command: Int, payload: ByteArray = ByteArray(0)) {
        outbox.addLast(gpSockResponse(GpSockType.Ack.code, command, payload))
    }

    private fun nak(command: Int, reason: NakCode) {
        // As the firmware sends it: the reason in the size slot, and no payload.
        outbox.addLast(gpSockRefusal(command, reason.code))
    }

    private fun chunked(command: Int, whole: ByteArray) {
        for (offset in whole.indices step GpSockConnection.MAX_CHUNK_PAYLOAD) {
            val size = minOf(GpSockConnection.MAX_CHUNK_PAYLOAD, whole.size - offset)
            ack(command, whole.copyOfRange(offset, offset + size))
        }

        ack(command)
    }
}

/** A file on the fake camera's card. */
class FakeFile(val code: Char, val index: Int, val taken: LocalDateTime, val content: ByteArray)

/** The size of the fake camera's Wi-Fi name and password fields. */
const val WIFI_FIELD_LENGTH = 32

/** A text value as the firmware stores it: zero-padded to its field. */
fun paddedField(text: String, length: Int): ByteArray {
    val field = ByteArray(length)
    text.toByteArray(Charsets.US_ASCII).copyInto(field)
    return field
}

// The protocol's own byte helpers are internal to it, so the fake keeps its own, little-endian as
// the firmware writes every number.
private fun ByteArray.uint16At(offset: Int): Int =
    (this[offset].toInt() and 0xFF) or
        ((this[offset + 1].toInt() and 0xFF) shl 8)

private fun ByteArray.putUInt16(offset: Int, value: Int) {
    this[offset] = value.toByte()
    this[offset + 1] = (value shr 8).toByte()
}

private fun ByteArray.putInt32(offset: Int, value: Int) {
    ByteBuffer.wrap(this, offset, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(value)
}
