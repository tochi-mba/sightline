package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.library.MediaSink
import com.rextechnologies.sightline.core.library.PendingMedia
import com.rextechnologies.sightline.core.library.SaveFailure
import com.rextechnologies.sightline.core.library.SniffingOutput
import com.rextechnologies.sightline.core.library.saving
import com.rextechnologies.sightline.core.link.CameraLink
import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.CameraLinkLease
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.session.CameraSession
import com.rextechnologies.sightline.core.session.CameraSessionTiming
import com.rextechnologies.sightline.core.session.CameraTimeoutException
import com.rextechnologies.sightline.core.session.LivePictureUnavailableException
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.gpsock.GpSockRefusedException
import com.rextechnologies.sightline.protocol.gpsock.MenuIds
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.DurationUnit
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/**
 * The camera, as the app drives it: connecting and staying connected, the live view, the shutter,
 * settings and the card.
 *
 * Everything here runs in [scope], which must use one thread: in the app, the main thread. Nothing
 * blocks it, because every socket call suspends onto the I/O dispatcher, and keeping every change to
 * this object on one thread is what makes it safe without locks of its own.
 *
 * Three rules shape it, each learnt from the reference camera:
 *
 * 1. **One camera operation at a time.** A shutter press, a setting and a status poll all go down one
 *    control channel, and an operation that switches the camera's mode must finish before another
 *    starts, or the second runs in the wrong mode.
 * 2. **The camera gives one live picture per power-on.** It answers one stream connection each time
 *    it is switched on, and browsing its card hangs that one up. So the stream is opened once and
 *    shared, a picture the camera will not give again is said rather than retried, and reading the
 *    card is something a screen asks before doing once the picture has been seen.
 * 3. **Nothing waits on the camera forever.** A camera whose Wi-Fi is half asleep still accepts a
 *    connection and then answers nothing, so every request has a deadline, a download has a stall
 *    detector, and a camera that stops answering is treated as lost and reconnected.
 */
class CameraController(
    private val scope: CoroutineScope,
    private val link: CameraLink,
    private val timing: ControllerTiming = ControllerTiming.Default,
    private val sessionTiming: CameraSessionTiming = CameraSessionTiming.Default,
    private val timeSource: TimeSource = TimeSource.Monotonic,
    /** Whether a lost camera is tried again, read each time one is lost: the person's setting. */
    private val reconnects: () -> Boolean = { true },
) {
    private val mutableState = MutableStateFlow(CameraState())
    private val mutableFrames = MutableStateFlow<LiveFrame?>(null)

    /** Everything the screens show about the camera. */
    val state: StateFlow<CameraState> = mutableState.asStateFlow()

    /**
     * The newest live picture, or null when there is none.
     *
     * Apart from [state] so twelve pictures a second do not make every screen that shows the camera's
     * status draw again.
     */
    val frames: StateFlow<LiveFrame?> = mutableFrames.asStateFlow()

    private val operations = Mutex()
    private val liveHolders = mutableSetOf<String>()

    /** Holders whose holding is itself the person asking for the picture, such as an armed Sentry. */
    private val askingHolders = mutableSetOf<String>()

    /** Asked for by the person on the live screen; cleared when the person disconnects, kept through a reconnect. */
    private var pictureAsked = false

    /** Whether a picture has arrived since the person last connected, so leaving can say what it costs. */
    private var pictureRan = false
    private var connecting: Job? = null
    private var current: Connected? = null
    private var live: Job? = null
    private var browsing = false

    /** The mode the camera last said it was in; read only once connected, after the first status. */
    private var cameraMode: CameraMode? = null
    private var frameNumber = 0L
    private var noticeNumber = 0L

    /**
     * Connects to the camera on [network] and stays connected until [disconnect], reconnecting when it
     * is lost.
     *
     * Ignored while a connection is already being made or held.
     */
    fun connect(network: CameraNetwork): Job? {
        if (connecting?.isActive == true) {
            return null
        }

        return scope.launch { stayConnected(network) }.also { connecting = it }
    }

    /**
     * Leaves the camera and its network.
     *
     * The camera stops recording when its control channel closes, so a screen asks before calling this
     * during a recording.
     */
    fun disconnect(): Job = scope.launch {
        connecting?.cancelAndJoin()
        connecting = null
        mutableFrames.value = null
        val ran = pictureRan
        pictureAsked = false
        pictureRan = false
        mutableState.update { CameraState(mode = it.mode) }
        if (ran) {
            // The camera is left with its buttons stuck; the person should hear it here, not find it out.
            notice(BUTTONS_STUCK)
        }
    }

    /**
     * Starts the live picture for whoever holds it, now that the person has asked: the camera gives it once
     * each time it starts, and its own buttons stay stuck once it has run, so it is never started unasked.
     */
    fun showLivePicture() {
        pictureAsked = true
        updateLive()
    }

    /**
     * Holds the live view open on behalf of [holder], such as the screen showing it or Sentry watching it.
     * It runs while anybody holds it and the camera is connected, once the picture has been asked for: by
     * [showLivePicture], or by a holder that is [asking], whose holding is itself the person's request, as
     * arming Sentry is. Letting go keeps the camera's one stream, and stops only the watching.
     */
    fun holdLive(holder: String, asking: Boolean = false) {
        liveHolders += holder
        if (asking) {
            askingHolders += holder
        }

        updateLive()
    }

    /** Lets go of the live view on behalf of [holder]. */
    fun releaseLive(holder: String) {
        liveHolders -= holder
        askingHolders -= holder
        updateLive()
    }

    /** Takes a photo or starts or stops recording, whichever the mode says. */
    fun shutter(): Job? = when (state.value.mode) {
        CaptureMode.Video -> toggleRecording()
        CaptureMode.Photo -> takePhoto()
    }

    /** Takes a photo onto the camera's card. */
    fun takePhoto(): Job? = perform(Task.TakingPhoto) { connected ->
        refuseWhileRecording("Stop recording to take a photo.")
        enterMode(connected, CameraMode.Capture)
        ask(connected) { it.capturePicture() }
        readStatus(connected)
        notice("Photo saved to the camera's card.")
    }

    /**
     * Starts or stops recording on the camera's card, and reports what the camera then says it is
     * doing rather than assuming the toggle worked.
     */
    fun toggleRecording(): Job? {
        val starting = !state.value.isRecording
        return perform(if (starting) Task.StartingRecording else Task.StoppingRecording) { connected ->
            if (starting) {
                // A recording is video, whichever button started it: the notification's and Sentry's can
                // start one from photo mode, and the app must not go on calling it photo mode.
                enterMode(connected, CameraMode.Record)
                mutableState.update { it.copy(mode = CaptureMode.Video) }
            }

            ask(connected) { it.toggleRecording() }
            val recording = readStatus(connected).isRecording
            notice(
                when {
                    starting && recording -> "Recording to the camera's card."
                    starting -> "The camera did not start recording."
                    recording -> "The camera is still recording."
                    else -> "Recording stopped and saved to the card."
                },
            )
        }
    }

    /**
     * Switches between video and photos. Before a camera is connected this only chooses what the
     * shutter will do; once connected, the camera is switched too, so its own screen agrees.
     */
    fun switchMode(mode: CaptureMode): Job? {
        if (mode == state.value.mode) {
            return null
        }

        if (!state.value.isConnected) {
            mutableState.update { it.copy(mode = mode) }
            return null
        }

        return perform(Task.SwitchingMode) { connected ->
            refuseWhileRecording("Stop recording to switch to ${mode.name.lowercase()}s.")
            enterMode(connected, mode.cameraMode)
            mutableState.update { it.copy(mode = mode) }
        }
    }

    /**
     * Sets the camera's setting [id] to the choice [value], then reads it back: a camera that ignores
     * a value it does not support still acknowledges it, so the read is what says whether it took.
     */
    fun changeSetting(id: Int, value: Int): Job? = perform(Task.ChangingSetting) { connected ->
        refuseWhileRecording("Settings cannot change while the camera records.")
        val setting = state.value.settings.firstOrNull { it.menu.id == id }
        if (setting == null || !setting.isChangeable || setting.menu.choices.none { it.value == value }) {
            throw Refusal("The camera does not offer that setting.")
        }

        ask(connected) { it.setSetting(id, value) }
        val now = ask(connected) { it.getChoice(id) }
        mutableState.update { state ->
            state.copy(settings = state.settings.map { if (it.menu.id == id) it.copy(value = now) else it })
        }
        if (now != value) {
            notice("The camera kept ${setting.menu.name} at ${setting.menu.labelFor(now)}.")
        }
        readStatus(connected)
    }

    /** Reads the list of files on the camera's card, then each one's thumbnail, newest first. */
    fun refreshLibrary(): Job? = perform(Task.ReadingCard) { connected ->
        refuseWhileRecording("Stop recording to look at the card.")
        mutableState.update { it.copy(library = it.library.copy(reading = true)) }
        try {
            browse(connected) {
                val files = askAtLength(connected) { it.getFileList() }
                mutableState.update { state ->
                    val library = state.library
                    state.copy(
                        library = library.copy(
                            files = files,
                            thumbnails = library.thumbnails.filterKeys {
                                it in
                                    files
                            },
                        ),
                    )
                }
                for (file in files.asReversed()) {
                    if (file !in state.value.library.thumbnails) {
                        fetchThumbnail(connected, file)
                    }
                }
            }
        } finally {
            mutableState.update { it.copy(library = it.library.copy(reading = false)) }
        }
    }

    /**
     * Copies [files] off the card into [sink], one after another, each published only once it is
     * whole. A file that fails is marked and the rest still copied, unless the camera itself was lost.
     *
     * @param deleteAfter Whether each file safely on the phone is then deleted from the card. Only a
     *   file that was published is deleted; one that failed stays on the card.
     */
    fun download(
        files: List<CameraFile>,
        sink: MediaSink,
        deleteAfter: Boolean = false,
    ): Job? = perform(Task.Copying) { connected ->
        refuseWhileRecording("Stop recording to copy from the card.")
        updateTransfers(files.associateWith { Transfer.Queued })
        try {
            browse(connected) {
                for (file in files) {
                    copy(connected, file, sink)
                }

                if (deleteAfter) {
                    deleteCopied(connected, files)
                }
            }
        } finally {
            // Whatever never started, because the copy was cancelled or the camera lost, says so.
            updateTransfers(
                state.value.library.transfers.filterValues { it == Transfer.Queued || it is Transfer.Copying }
                    .mapValues { Transfer.Failed("Not copied: the copy was stopped.") },
            )
        }
    }

    private suspend fun deleteCopied(connected: Connected, files: List<CameraFile>) {
        val saved = files.filter { state.value.library.transfers[it] is Transfer.Saved }
        if (saved.isEmpty()) {
            return
        }

        // Highest index first, as for any delete, so a renumbering camera cannot shift a file still to go.
        for (file in saved.sortedByDescending { it.index }) {
            ask(connected) { it.deleteFile(file.index) }
        }

        val remaining = askAtLength(connected) { it.getFileList() }
        mutableState.update { state ->
            val library = state.library
            state.copy(
                library = library.copy(
                    files = remaining,
                    thumbnails = library.thumbnails.filterKeys { it in remaining },
                ),
            )
        }
    }

    /** Deletes [files] from the camera's card, then reads the card again. */
    fun delete(files: List<CameraFile>): Job? = perform(Task.Deleting) { connected ->
        refuseWhileRecording("Stop recording to delete from the card.")
        browse(connected) {
            // Highest index first, so a camera that renumbers after a delete cannot shift a file still
            // to be deleted onto a file that was meant to stay.
            for (file in files.sortedByDescending { it.index }) {
                ask(connected) { it.deleteFile(file.index) }
            }

            val remaining = askAtLength(connected) { it.getFileList() }
            mutableState.update { state ->
                val library = state.library
                state.copy(
                    library = library.copy(
                        files = remaining,
                        thumbnails = library.thumbnails.filterKeys {
                            it in
                                remaining
                        },
                    ),
                )
            }
        }
        notice(if (files.size == 1) "Deleted from the card." else "${files.size} files deleted from the card.")
    }

    /** Clears the notice numbered [id], unless a newer one has replaced it. */
    fun dismissNotice(id: Long) {
        mutableState.update { if (it.notice?.id == id) it.copy(notice = null) else it }
    }

    private suspend fun stayConnected(requested: CameraNetwork) {
        var network = requested
        var attempt = 0
        var problem: Problem? = null
        while (true) {
            if (problem != null) {
                if (attempt >= timing.reconnectAttempts) {
                    mutableState.update { it.copy(connection = Connection.Failed(problem)) }
                    return
                }

                attempt++
                mutableState.update {
                    it.copy(connection = Connection.Reconnecting(attempt, timing.reconnectAttempts, problem))
                }
                delay(timing.reconnectDelay * attempt)
            } else {
                mutableState.update { it.copy(connection = Connection.Joining(network)) }
            }

            when (val outcome = connectOnce(network, reconnecting = problem != null)) {
                is Attempt.Unreachable -> {
                    if (problem == null) {
                        // The first attempt failing is shown at once: the person is there, waiting.
                        mutableState.update { it.copy(connection = Connection.Failed(outcome.problem)) }
                        return
                    }

                    // A reconnect that fails says why it last failed: Wi-Fi switched off in the
                    // meantime is more use to know than that the camera was lost in the first place.
                    problem = outcome.problem
                }

                is Attempt.Dropped -> {
                    if (!reconnects()) {
                        mutableState.update { it.copy(connection = Connection.Failed(outcome.problem)) }
                        return
                    }

                    // Once the camera's name is known, ask for exactly it, which Android can grant with
                    // no dialog in the way of a reconnect.
                    network = state.value.cameraName?.let(network::exactly) ?: network
                    attempt = 0
                    problem = outcome.problem
                }
            }
        }
    }

    private suspend fun connectOnce(network: CameraNetwork, reconnecting: Boolean): Attempt {
        val lease = try {
            link.join(network)
        } catch (failure: CameraLinkException) {
            return Attempt.Unreachable(Problem.whileJoining(failure))
        }

        try {
            if (!reconnecting) {
                mutableState.update { it.copy(connection = Connection.Opening) }
            }

            val session = try {
                CameraSession.open(lease::transport, scope, timing = sessionTiming, timeSource = timeSource)
            } catch (failure: Exception) {
                rethrowCancellation(failure)
                return Attempt.Unreachable(Problem.whileTalking(failure))
            }

            val connected =
                Connected(
                    session,
                    lease,
                    CoroutineScope(scope.coroutineContext + SupervisorJob(scope.coroutineContext[Job])),
                )
            try {
                try {
                    readCamera(connected)
                } catch (failure: Exception) {
                    rethrowCancellation(failure)
                    return Attempt.Unreachable(Problem.whileTalking(failure))
                }

                current = connected
                mutableState.update { it.copy(connection = Connection.Connected) }
                updateLive()
                return Attempt.Dropped(watch(connected))
            } finally {
                current = null
                withContext(NonCancellable) {
                    // The stream first, while its RTSP session can still be ended politely. Then the
                    // operations are cancelled, so one waiting on the camera ends as cancelled rather
                    // than as a camera that failed; then the control channel closes, which ends any read
                    // that ignored the cancelling; and only then are the operations waited for.
                    stopLive()
                    val operations = connected.work.coroutineContext.job
                    operations.cancel()
                    session.close()
                    operations.join()
                }
            }
        } finally {
            lease.close()
        }
    }

    /** Reads what the screens need about a camera just connected: its status, settings and name. */
    private suspend fun readCamera(connected: Connected) {
        val status = readStatus(connected)
        val mode = when (status.mode) {
            CameraMode.Capture -> CaptureMode.Photo
            CameraMode.Record -> CaptureMode.Video
            // Left browsing, or in a mode this app does not know: put it where the person last was.
            else -> state.value.mode
        }
        enterMode(connected, mode.cameraMode)

        val menu = askAtLength(connected) { it.getMenu() }
        val settings = menu.settings.map { setting ->
            when (setting.kind) {
                MenuSettingKind.Choice -> CameraSetting(
                    setting,
                    readOrNull {
                        ask(connected) { it.getChoice(setting.id) }
                    },
                    null,
                )
                MenuSettingKind.Text, MenuSettingKind.ReadOnly ->
                    CameraSetting(
                        setting,
                        null,
                        readOrNull {
                            ask(connected) { it.getText(setting.id) }
                        }?.takeIf(String::isNotEmpty),
                    )
                else -> CameraSetting(setting, null, null)
            }
        }
        val name = settings.firstOrNull { it.menu.id == MenuIds.WIFI_NAME }?.text
        mutableState.update { it.copy(mode = mode, settings = settings, cameraName = name) }
    }

    /**
     * Holds the connection until it is lost, polling the camera's status meanwhile: that is what keeps
     * the recording indicator true when somebody presses the camera's own button.
     *
     * @return Why the connection ended.
     */
    private suspend fun watch(connected: Connected): Problem = coroutineScope {
        val watchers = launch {
            launch {
                connected.lease.awaitLoss()
                connected.lose(Problem(ProblemKind.Lost, "Android lost the camera's network."))
            }
            launch {
                while (true) {
                    delay(timing.statusInterval)
                    try {
                        operations.withLock { readStatus(connected) }
                    } catch (failure: GpSockRefusedException) {
                        // The camera is there and said no; the next poll asks again.
                    } catch (failure: Exception) {
                        rethrowCancellation(failure)
                        connected.lose(Problem.whileTalking(failure))
                    }
                }
            }
        }

        connected.lost.await().also { watchers.cancel() }
    }

    /**
     * Runs one operation the person asked for, after the ones before it, with [task] shown meanwhile.
     *
     * A refusal from the camera, or from the app on the camera's behalf, becomes a notice; anything else
     * means the camera stopped answering, and the connection is treated as lost.
     */
    private fun perform(task: Task, operation: suspend (Connected) -> Unit): Job? {
        val connected = current
        if (connected == null) {
            notice(
                if (state.value.connection is Connection.Reconnecting) {
                    "The camera is reconnecting. Try again in a moment."
                } else {
                    "Connect to the camera first."
                },
            )
            return null
        }

        // Checked and marked in one step, so a second press from another thread cannot slip in between.
        var claimed = false
        mutableState.update {
            claimed = it.task == null
            if (claimed) it.copy(task = task) else it
        }
        if (!claimed) {
            // Something is already in progress; the screens disable what would clash with it.
            return null
        }

        val job = connected.work.launch {
            try {
                operations.withLock { operation(connected) }
            } catch (refused: GpSockRefusedException) {
                notice("The camera said no: ${GpSockRefusedException.explain(refused.code)}.")
            } catch (refused: Refusal) {
                notice(refused.message)
            } catch (failure: Exception) {
                rethrowCancellation(failure)
                connected.lose(Problem.whileTalking(failure))
            }
        }
        // On completion rather than in a finally: a job cancelled before it started never runs one.
        job.invokeOnCompletion { mutableState.update { it.copy(task = null) } }
        return job
    }

    private fun refuseWhileRecording(message: String) {
        if (state.value.isRecording) {
            throw Refusal(message)
        }
    }

    /** Puts the camera in [mode] unless its last status already said it was there. */
    private suspend fun enterMode(connected: Connected, mode: CameraMode) {
        if (cameraMode != mode) {
            ask(connected) { it.setMode(mode) }
            readStatus(connected)
        }
    }

    private suspend fun readStatus(connected: Connected): CameraStatus {
        val status = CameraStatus.of(ask(connected) { it.getStatus() })
        cameraMode = status.mode
        mutableState.update { it.copy(status = status) }
        return status
    }

    /**
     * Runs [block] with the camera browsing its card and the live view stopped, and puts both back
     * after, however [block] ends.
     */
    private suspend fun browse(connected: Connected, block: suspend () -> Unit) {
        browsing = true
        stopLive()
        try {
            ask(connected) { it.setMode(CameraMode.Browse) }
            block()
        } finally {
            withContext(NonCancellable) {
                // Best effort and bounded: a camera that is gone is found by the next poll. The status
                // read after is what keeps the app's idea of the camera's mode true either way.
                withTimeoutOrNull(timing.answer) {
                    runCatching {
                        connected.session.control.setMode(state.value.mode.cameraMode)
                        val status = CameraStatus.of(connected.session.control.getStatus())
                        cameraMode = status.mode
                        mutableState.update { it.copy(status = status) }
                    }
                }
                browsing = false
                // The camera hung up its stream to browse, and will not answer another until it restarts.
                mutableState.update { it.copy(holdsLivePicture = false) }
                updateLive()
            }
        }
    }

    private suspend fun fetchThumbnail(connected: Connected, file: CameraFile) {
        val jpeg = try {
            ask(connected) { it.getThumbnail(file.index) }
        } catch (refused: GpSockRefusedException) {
            // A file the camera has no thumbnail for is shown by its kind instead.
            return
        }

        mutableState.update { state ->
            state.copy(library = state.library.copy(thumbnails = state.library.thumbnails + (file to Thumbnail(jpeg))))
        }
    }

    private suspend fun copy(connected: Connected, file: CameraFile, sink: MediaSink) {
        var pending: PendingMedia? = null
        val output = SniffingOutput { kind -> sink.create(file, kind).also { pending = it }.output }
        updateTransfers(mapOf(file to Transfer.Copying(0, file.approximateBytes)))
        try {
            withStallDetector { progressed ->
                connected.session.control.download(file.index, output) { bytes ->
                    progressed()
                    updateTransfers(mapOf(file to Transfer.Copying(bytes, file.approximateBytes)))
                }
            }
            output.finish()
            // Nothing is created until the first bytes arrive, so no destination means no bytes.
            val saved = pending ?: throw Refusal("The camera sent nothing for this file.")
            val where = saving {
                saved.output.close()
                saved.publish()
            }
            updateTransfers(mapOf(file to Transfer.Saved(where)))
        } catch (failure: Exception) {
            // Discarding is tidying up; a failure there must not hide why the copy failed.
            runCatching { pending?.discard() }
            val reason = when (failure) {
                is GpSockRefusedException -> "The camera said no: ${GpSockRefusedException.explain(failure.code)}."
                is Refusal -> failure.message
                is SaveFailure -> "The phone could not save it: ${failure.message}"
                else -> null
            }
            if (reason == null) {
                // Cancelled, or the camera stopped answering: the rest cannot be copied either.
                throw failure
            }

            updateTransfers(mapOf(file to Transfer.Failed(reason)))
        }
    }

    /**
     * Runs [transfer], failing it with [CameraTimeoutException] if it goes [ControllerTiming.transferStall]
     * without calling the progress function it is given.
     */
    private suspend fun <T> withStallDetector(transfer: suspend (progressed: () -> Unit) -> T): T = coroutineScope {
        var lastProgress = timeSource.markNow()
        val detector = launch {
            while (true) {
                delay(timing.transferStall / 4)
                if (lastProgress.elapsedNow() >= timing.transferStall) {
                    throw CameraTimeoutException(
                        "The camera stopped sending for ${timing.transferStall.inWholeSeconds} seconds.",
                    )
                }
            }
        }

        transfer { lastProgress = timeSource.markNow() }.also { detector.cancel() }
    }

    private fun updateTransfers(changes: Map<CameraFile, Transfer>) {
        mutableState.update { it.copy(library = it.library.copy(transfers = it.library.transfers + changes)) }
    }

    private fun updateLive() {
        val connected = current
        val asked = pictureAsked || askingHolders.isNotEmpty()
        val wanted = connected != null && liveHolders.isNotEmpty() && !browsing && asked
        // A live job only ever ends by being cancelled here or in stopLive, both of which forget it, so
        // one that is remembered is running.
        val running = live
        if (wanted && running == null) {
            live = scope.launch { runLive(connected) }
        } else if (!wanted && running != null) {
            running.cancel()
            live = null
        }

        val shown = when {
            connected == null || liveHolders.isEmpty() -> LiveView.Off
            browsing -> LiveView.Paused
            !asked -> LiveView.Offered
            else -> null
        }
        if (shown != null) {
            mutableState.update { it.copy(live = shown) }
        }
    }

    private suspend fun stopLive() {
        live?.cancelAndJoin()
        live = null
        updateLive()
    }

    /**
     * Shows the camera's pictures in [frames] until cancelled. A quiet spell is said and waited out on the
     * same stream; a picture the camera will not give again ends it.
     */
    private suspend fun runLive(connected: Connected) {
        mutableState.update { it.copy(live = LiveView.Starting) }
        while (true) {
            var window: TimeMark? = null
            var windowFrames = 0
            try {
                connected.session.frames().collect { frame ->
                    mutableFrames.value = LiveFrame(frame.jpeg, frame.width, frame.height, ++frameNumber)
                    val started = window
                    if (started == null) {
                        // The first picture starts the count: how long the stream took to start is not
                        // its frame rate, and counting it would understate every first second.
                        window = timeSource.markNow()
                        pictureRan = true
                        mutableState.update { it.copy(live = LiveView.Playing(0.0), holdsLivePicture = true) }
                    } else {
                        windowFrames++
                        val elapsed = started.elapsedNow()
                        if (elapsed >= 1.seconds) {
                            val perSecond = windowFrames / elapsed.toDouble(DurationUnit.SECONDS)
                            mutableState.update { it.copy(live = LiveView.Playing(perSecond)) }
                            window = timeSource.markNow()
                            windowFrames = 0
                        }
                    }
                }
            } catch (quiet: CameraTimeoutException) {
                // The stream is still open; watching it again costs nothing.
                mutableState.update { it.copy(live = LiveView.Interrupted(quiet.message)) }
            } catch (unavailable: LivePictureUnavailableException) {
                // Asking again would open a connection the camera never answers, so this is where it stops.
                mutableState.update {
                    it.copy(live = LiveView.Unavailable(unavailable.message), holdsLivePicture = false)
                }
                return
            }
        }
    }

    /** One request to the camera, given [ControllerTiming.answer] to come back. */
    private suspend fun <T : Any> ask(connected: Connected, request: suspend (GpSockConnection) -> T): T =
        askWithin(connected, timing.answer, request)

    /** A request whose answer can run to many frames, such as the menu or the card's whole file list. */
    private suspend fun <T : Any> askAtLength(connected: Connected, request: suspend (GpSockConnection) -> T): T =
        askWithin(connected, timing.longAnswer, request)

    private suspend fun <T : Any> askWithin(
        connected: Connected,
        limit: Duration,
        request: suspend (GpSockConnection) -> T,
    ): T = withTimeoutOrNull(limit) { request(connected.session.control) }
        ?: throw CameraTimeoutException("The camera did not answer within ${limit.inWholeSeconds} seconds.")

    private suspend fun <T> readOrNull(read: suspend () -> T): T? = try {
        read()
    } catch (refused: GpSockRefusedException) {
        null
    }

    private fun notice(text: String) {
        mutableState.update { it.copy(notice = Notice(++noticeNumber, text)) }
    }

    private fun rethrowCancellation(failure: Exception) {
        if (failure is CancellationException) {
            throw failure
        }
    }

    /**
     * The camera, while connected: its session, its network, the operations running on it, and the one
     * signal that it was lost.
     */
    private class Connected(val session: CameraSession, val lease: CameraLinkLease, val work: CoroutineScope) {
        val lost = CompletableDeferred<Problem>()

        /** Ends the connection for [problem]; the first problem reported is the one kept. */
        fun lose(problem: Problem) {
            lost.complete(problem)
        }
    }

    /** How one attempt to connect ended. */
    private sealed interface Attempt {
        /** It never connected. */
        data class Unreachable(val problem: Problem) : Attempt

        /** It connected, and was later lost. */
        data class Dropped(val problem: Problem) : Attempt
    }

    /** The app declining something on the camera's behalf, with the reason to show. */
    private class Refusal(override val message: String) : Exception(message)
}

/**
 * How long the controller gives the camera.
 *
 * @property answer For one request to be answered.
 * @property longAnswer For a request answered in many frames: the menu, or the card's file list.
 * @property transferStall For a download to go without a byte before it is called stalled.
 * @property statusInterval Between status polls while connected.
 * @property reconnectDelay After a camera is lost, times the attempt number, before trying again.
 * @property reconnectAttempts How many times a lost camera is tried before giving up.
 */
data class ControllerTiming(
    val answer: Duration,
    val longAnswer: Duration,
    val transferStall: Duration,
    val statusInterval: Duration,
    val reconnectDelay: Duration,
    val reconnectAttempts: Int,
) {
    companion object {
        /** The timings the app uses. */
        val Default = ControllerTiming(
            answer = 10.seconds,
            longAnswer = 60.seconds,
            transferStall = 15.seconds,
            statusInterval = 2.seconds,
            reconnectDelay = 3.seconds,
            reconnectAttempts = 5,
        )
    }
}
