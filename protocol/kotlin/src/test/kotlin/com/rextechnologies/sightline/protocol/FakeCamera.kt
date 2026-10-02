package com.rextechnologies.sightline.protocol

import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockCommand
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.gpsock.GpSockFrame
import com.rextechnologies.sightline.protocol.gpsock.GpSockType
import com.rextechnologies.sightline.protocol.gpsock.NakCode
import kotlinx.coroutines.yield
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * A camera that needs no hardware, speaking the real wire format.
 *
 * It answers the way the reference camera did, including the behaviours that are easy to get
 * wrong: long answers arrive in chunks ended by an empty one, browsing is refused unless the
 * camera was put in browse mode first, and the frames can be delivered in awkward pieces so the
 * reader is exercised the way TCP really exercises it.
 */
class FakeCamera : CameraTransport {
    private val outbox = ArrayDeque<ByteArray>()
    private val inbox = mutableListOf<Byte>()

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

    /** Which mode the camera is in. Browsing is refused in any other. */
    var mode = CameraMode.Record
        private set

    /** Whether the camera is recording to its card. */
    var isRecording = false
        private set

    /** Whether the media flow has been started. */
    var isStreaming = false
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

    /** The settings this camera has been told to change, as id and value. */
    val settingsWritten = mutableListOf<Pair<Int, Int>>()

    /** How many files it claims are on the card. */
    var fileCount = 2

    /** The status payload it reports; 16 bytes, as the reference camera sends. */
    var status: ByteArray = "000280010025b3000000fe7c0000a501".hexToByteArray()

    /**
     * Acknowledgements to give in place of the reference camera's, by command, for the answers a
     * firmware might give that the reference camera has not been seen to.
     */
    val answers = mutableMapOf<GpSockCommand, ByteArray>()

    override var isConnected = false
        private set

    /** Whether anything closed the connection, which a real camera reacts badly to. */
    var wasDisposed = false
        private set

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
            if (!hangsUp) {
                handle(command, payload)
            }
        }
    }

    override suspend fun receive(into: ByteArray): Int {
        if (answersSlowly) {
            yield()
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
    }

    private fun handle(command: Int, payload: ByteArray) {
        val known = GpSockCommand.fromCode(command)
        val scripted = known?.let { answers[it] }
        if (scripted != null) {
            ack(command, scripted)
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
                if (mode != CameraMode.Browse) {
                    nak(command, NakCode.ServerBusy)
                } else {
                    ack(command, bytes(fileCount and 0xFF, fileCount shr 8))
                }

            GpSockCommand.MenuSetParameter -> {
                val id = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN).int
                settingsWritten += id to (payload[5].toInt() and 0xFF)
                ack(command)
            }

            else -> nak(command, NakCode.InvalidCommand)
        }
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
