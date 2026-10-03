package com.rextechnologies.sightline.camera

import android.graphics.BitmapFactory
import android.net.Network
import android.os.SystemClock
import com.rextechnologies.sightline.core.model.AppModel
import com.rextechnologies.sightline.core.model.CameraLibrary
import com.rextechnologies.sightline.core.model.ConnectionState
import com.rextechnologies.sightline.core.model.HudReading
import com.rextechnologies.sightline.core.sentry.MotionDetector
import com.rextechnologies.sightline.network.CameraNetworkLease
import com.rextechnologies.sightline.network.LocalOnlyCameraNetwork
import com.rextechnologies.sightline.network.NetworkCameraTransport
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.RtpJpegReassembler
import com.rextechnologies.sightline.protocol.rtp.RtspClient
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.Closeable
import java.net.InetSocketAddress

/** Owns the one control connection and every stream socket for one camera session. */
class CameraEngine(
    private val scope: CoroutineScope,
    private val networks: LocalOnlyCameraNetwork,
) : Closeable {
    private val mutableState = MutableStateFlow(AppModel())
    private val commands = Mutex()
    private var lease: CameraNetworkLease? = null
    private var control: GpSockConnection? = null
    private var stream: Job? = null
    private var frameCount = 0
    private var frameWindowStarted = 0L
    private val motion = MotionDetector()

    val state: StateFlow<AppModel> = mutableState.asStateFlow()

    suspend fun connect(name: String, password: String) {
        disconnect()
        mutableState.value = AppModel(connection = ConnectionState.Connecting(name))
        try {
            val acquired = networks.request(name, password)
            lease = acquired
            val opened = GpSockConnection(transport(acquired.network, GpSockConnection.PORT))
            opened.open()
            control = opened
            opened.getStatus()
            val menu = opened.getMenu()
            mutableState.value = mutableState.value.copy(
                connection = ConnectionState.Connected(name),
                // This firmware's busy bit also means playback, so it is not shown as recording.
                recording = false,
                settings = menu.settings,
                message = "Camera connected locally. Your internet route was not changed.",
            )
            startStream()
        } catch (failure: Throwable) {
            if (failure is CancellationException) throw failure
            release()
            mutableState.value = AppModel(
                connection = ConnectionState.Failed(failure.message ?: "The camera did not answer."),
                message = failure.message,
            )
        }
    }

    suspend fun disconnect() {
        stream?.cancelAndJoin()
        stream = null
        release()
        mutableState.value = AppModel(message = "Disconnected. Mobile data and hotspot were left alone.")
    }

    suspend fun capture() = command("Photo saved to the camera card.") { connection ->
        connection.setMode(CameraMode.Capture)
        try {
            connection.capturePicture()
        } finally {
            connection.setMode(CameraMode.Record)
        }
    }

    suspend fun toggleRecording() = command(null) { connection ->
        connection.toggleRecording()
        mutableState.value = mutableState.value.copy(
            recording = !mutableState.value.recording,
            message = if (mutableState.value.recording) "Recording on the camera card." else "Recording stopped.",
        )
    }

    suspend fun writeSetting(id: Int, value: Int) = command("Setting updated on the camera.") { connection ->
        connection.setSetting(id, value)
    }

    suspend fun refreshLibrary() {
        val connection = control ?: return disconnectedMessage()
        mutableState.value = mutableState.value.copy(library = CameraLibrary(loading = true), message = null)
        stopStream()
        try {
            commands.withLock {
                connection.setMode(CameraMode.Browse)
                val count = connection.getFileCount()
                mutableState.value = mutableState.value.copy(library = CameraLibrary(count), message = null)
                connection.setMode(CameraMode.Record)
            }
        } catch (failure: Throwable) {
            mutableState.value = mutableState.value.copy(
                library = CameraLibrary(),
                message = failure.message ?: "The camera card could not be read.",
            )
            runCatching { commands.withLock { connection.setMode(CameraMode.Record) } }
        } finally {
            startStream()
        }
    }

    fun setSentryRunning(running: Boolean) {
        mutableState.value = mutableState.value.copy(sentryRunning = running)
    }

    fun noteMotion() {
        mutableState.value = mutableState.value.copy(
            motionEvents = mutableState.value.motionEvents + 1,
            message = "Motion detected.",
        )
    }

    fun setHudVisible(visible: Boolean) {
        mutableState.value = mutableState.value.copy(hudVisible = visible)
    }

    fun updateHud(reading: HudReading) {
        mutableState.value = mutableState.value.copy(hud = reading)
    }

    private suspend fun command(success: String?, body: suspend (GpSockConnection) -> Unit) {
        val connection = control ?: return disconnectedMessage()
        try {
            commands.withLock { body(connection) }
            if (success != null) mutableState.value = mutableState.value.copy(message = success)
        } catch (failure: Throwable) {
            if (failure is CancellationException) throw failure
            mutableState.value = mutableState.value.copy(message = failure.message ?: "The camera refused the command.")
        }
    }

    private fun disconnectedMessage() {
        mutableState.value = mutableState.value.copy(message = "Connect to a camera first.")
    }

    private fun startStream() {
        val cameraNetwork = lease?.network ?: return
        if (stream?.isActive == true) return
        frameCount = 0
        frameWindowStarted = SystemClock.elapsedRealtime()
        stream = scope.launch { stream(cameraNetwork) }
    }

    private suspend fun stopStream() {
        stream?.cancelAndJoin()
        stream = null
    }

    private suspend fun stream(cameraNetwork: Network) {
        val rtsp = RtspClient(transport(cameraNetwork, RtspClient.PORT), CAMERA_HOST)
        try {
            commands.withLock { control?.startStreaming() ?: return }
            rtsp.connect()
            requireSuccess("OPTIONS", rtsp.options().isSuccess)
            requireSuccess("DESCRIBE", rtsp.describe().isSuccess)
            requireSuccess("SETUP", rtsp.setupVideo().isSuccess)
            requireSuccess("PLAY", rtsp.play().isSuccess)
            val reassembler = RtpJpegReassembler()
            while (true) {
                val bytes = rtsp.readStream()
                if (bytes.isEmpty()) throw RtspException("The camera closed the live stream.")
                for (frame in reassembler.push(bytes)) {
                    if (RtpJpegReassembler.looksLikeJpeg(frame.jpeg)) publishFrame(frame.jpeg)
                }
            }
        } catch (failure: Throwable) {
            if (failure !is CancellationException) {
                mutableState.value = mutableState.value.copy(message = failure.message ?: "Live view stopped.")
            }
        } finally {
            withContext(NonCancellable) {
                if (rtsp.session != null) withTimeoutOrNull(2_000) { runCatching { rtsp.teardown() } }
                rtsp.close()
            }
        }
    }

    private fun publishFrame(jpeg: ByteArray) {
        frameCount++
        val now = SystemClock.elapsedRealtime()
        val elapsed = now - frameWindowStarted
        val fps = if (elapsed >= 1_000) {
            val measured = frameCount * 1_000.0 / elapsed
            frameCount = 0
            frameWindowStarted = now
            measured
        } else {
            mutableState.value.framesPerSecond
        }
        mutableState.value = mutableState.value.copy(jpeg = jpeg, framesPerSecond = fps)
        if (mutableState.value.sentryRunning && motion.observe(sampleLuminance(jpeg))) noteMotion()
    }

    private fun sampleLuminance(jpeg: ByteArray): ByteArray {
        val options = BitmapFactory.Options().apply { inSampleSize = 16 }
        val bitmap = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, options) ?: return byteArrayOf(0)
        val pixels = IntArray(bitmap.width * bitmap.height)
        bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
        bitmap.recycle()
        return ByteArray(pixels.size) { index ->
            val colour = pixels[index]
            val red = colour shr 16 and 0xFF
            val green = colour shr 8 and 0xFF
            val blue = colour and 0xFF
            ((red * 30 + green * 59 + blue * 11) / 100).toByte()
        }
    }

    private fun transport(network: Network, port: Int) =
        NetworkCameraTransport(network, InetSocketAddress(CAMERA_HOST, port))

    private fun requireSuccess(operation: String, success: Boolean) {
        if (!success) throw RtspException("The camera refused RTSP $operation.")
    }

    private fun release() {
        control?.close()
        control = null
        lease?.close()
        lease = null
        motion.reset()
    }

    override fun close() {
        stream?.cancel()
        release()
    }

    private companion object {
        const val CAMERA_HOST = "192.168.100.1"
    }
}
