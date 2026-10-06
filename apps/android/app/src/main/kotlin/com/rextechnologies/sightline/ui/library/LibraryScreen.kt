package com.rextechnologies.sightline.ui.library

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.camera.Thumbnail
import com.rextechnologies.sightline.core.camera.Transfer
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.design.EmptyState
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.PageHeading
import com.rextechnologies.sightline.design.ProgressTrack
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexDialog
import com.rextechnologies.sightline.design.RexShapes
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SectionLabel
import com.rextechnologies.sightline.design.SignalButton
import com.rextechnologies.sightline.design.Tone
import com.rextechnologies.sightline.design.rexClickable
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraFileKind
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * What is on the camera's card: thumbnails by day, newest first, to copy to the phone or delete.
 *
 * The camera lists its card only in browse mode, which ends its live picture until it is switched off
 * and on. So the card is read by itself when this screen opens only while that costs nothing; once the
 * picture has been seen, the screen says what reading the card will cost and waits to be asked.
 */
@Composable
fun LibraryScreen(graph: AppGraph, camera: CameraState) {
    val library = camera.library
    var selected by remember { mutableStateOf(emptySet<CameraFile>()) }
    var confirmDelete by remember { mutableStateOf(false) }

    LaunchedEffect(camera.isConnected) {
        if (camera.isConnected && library.files == null && !camera.holdsLivePicture) {
            graph.controller.refreshLibrary()
        }
    }

    Column(Modifier.fillMaxSize().padding(horizontal = RexSpace.PageMargin)) {
        PageHeading(
            eyebrow = "Camera card",
            headline = library.files?.let { countWords(it) } ?: "The card",
            statusText = if (library.reading) "Reading" else null,
            modifier = Modifier.padding(vertical = RexSpace.Medium),
        )

        val files = library.files
        when {
            !camera.isConnected -> EmptyState(
                "C",
                "No camera connected",
                "Connect a camera to see what is on its card.",
            )
            files == null && library.reading -> EmptyState("…", "Reading the card", "The camera is listing its files.")
            files == null && camera.holdsLivePicture -> EmptyState(
                "!",
                "The card",
                "Reading the card ends the live picture, which then needs the camera's battery taken out and " +
                    "put back: it cannot show its picture and list its files at once.",
            )
            files == null -> EmptyState("…", "The card", "Read the card to see what is on it.")
            files.isEmpty() -> EmptyState(
                "0",
                "The card is empty",
                "The camera says its card is empty, or that it has no card.",
            )

            else -> Box(Modifier.weight(1f)) {
                FileGrid(files, library.thumbnails, library.transfers, selected) { file ->
                    selected = if (file in selected) selected - file else selected + file
                }
            }
        }

        Row(
            Modifier.fillMaxWidth().padding(vertical = RexSpace.Compact),
            horizontalArrangement = Arrangement.spacedBy(RexSpace.Small),
        ) {
            if (selected.isEmpty()) {
                OutlineAction(
                    text = if (files == null) "Read the card" else "Read the card again",
                    onClick = { graph.controller.refreshLibrary() },
                    enabled = camera.isConnected && camera.task == null,
                    modifier = Modifier.weight(1f),
                )
            } else {
                SignalButton(
                    text = "Copy ${selected.size} to phone",
                    onClick = {
                        graph.controller.download(
                            selected.sortedBy { it.index },
                            graph.gallery,
                            deleteAfter = graph.settings[AppSettings.DeleteAfterCopy],
                        )
                        selected = emptySet()
                    },
                    enabled = camera.task == null,
                    modifier = Modifier.weight(1f),
                )
                OutlineAction(
                    text = "Delete",
                    onClick = { confirmDelete = true },
                    enabled = camera.task == null,
                    tone = Tone.Live,
                )
            }
        }
    }

    if (confirmDelete) {
        RexDialog(
            title = if (selected.size ==
                1
            ) {
                "Delete this file from the camera?"
            } else {
                "Delete ${selected.size} files from the camera?"
            },
            body = "They are removed from the camera's card. Anything already copied to your phone stays there.",
            confirm = "Delete" to {
                graph.controller.delete(selected.toList())
                selected = emptySet()
                confirmDelete = false
            },
            destructive = true,
            onDismiss = { confirmDelete = false },
        )
    }
}

@Composable
private fun FileGrid(
    files: List<CameraFile>,
    thumbnails: Map<CameraFile, Thumbnail>,
    transfers: Map<CameraFile, Transfer>,
    selected: Set<CameraFile>,
    toggle: (CameraFile) -> Unit,
) {
    val days = remember(files) { byDay(files) }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 104.dp),
        horizontalArrangement = Arrangement.spacedBy(RexSpace.Small),
        verticalArrangement = Arrangement.spacedBy(RexSpace.Small),
    ) {
        days.forEach { (day, onDay) ->
            item(span = { GridItemSpan(maxLineSpan) }, key = "day-$day") {
                SectionLabel(text = day, modifier = Modifier.padding(top = RexSpace.Small))
            }
            items(onDay, key = { "${it.code}-${it.index}" }) { file ->
                FileTile(file, thumbnails[file], transfers[file], file in selected) { toggle(file) }
            }
        }
    }
}

@Composable
private fun FileTile(
    file: CameraFile,
    thumbnail: Thumbnail?,
    transfer: Transfer?,
    chosen: Boolean,
    onClick: () -> Unit,
) {
    val picture = remember(thumbnail) {
        thumbnail?.let { BitmapFactory.decodeByteArray(it.jpeg, 0, it.jpeg.size)?.asImageBitmap() }
    }
    Column(
        Modifier
            .semantics {
                selected = chosen
                contentDescription = describe(file)
            }
            .rexClickable(onClick = onClick)
            .border(
                if (chosen) 2.dp else RexSpace.Hairline,
                if (chosen) RexColors.Signal else RexColors.Line,
                RexShapes.Small,
            )
            .padding(4.dp),
    ) {
        Box(
            Modifier.fillMaxWidth().aspectRatio(16f / 9f).background(RexColors.Raised, RexShapes.Small),
            contentAlignment = Alignment.Center,
        ) {
            if (picture != null) {
                Image(
                    picture,
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            } else {
                RexText(text = kindWord(file.kind).take(1), style = RexType.TitleLarge.copy(color = RexColors.Muted))
            }
        }
        RexText(
            text = kindWord(file.kind).uppercase(Locale.ROOT),
            style = RexType.LabelSmall,
            modifier = Modifier.padding(top = 4.dp),
        )
        RexText(text = sizeWords(file.approximateBytes), style = RexType.BodySmall)
        TransferLine(transfer)
    }
}

@Composable
private fun TransferLine(transfer: Transfer?) {
    when (transfer) {
        null -> Unit
        Transfer.Queued -> RexText(text = "Waiting to copy", style = RexType.BodySmall)
        is Transfer.Copying -> ProgressTrack(
            fraction = if (transfer.expected > 0) transfer.copied.toFloat() / transfer.expected else null,
            contentDescription = "Copying",
        )
        is Transfer.Saved -> RexText(text = "On your phone", style = RexType.BodySmall.copy(color = RexColors.Signal))
        is Transfer.Failed -> RexText(text = transfer.reason, style = RexType.BodySmall.copy(color = RexColors.Live))
    }
}

/** Files by the day they were taken, newest day and newest file first; undated ones last. */
fun byDay(files: List<CameraFile>): List<Pair<String, List<CameraFile>>> {
    val dated = files.filter { it.taken != null }.sortedByDescending { it.taken }
    val days = dated.groupBy { DAY.format(it.taken) }.toList()
    val undated = files.filter { it.taken == null }
    return if (undated.isEmpty()) days else days + ("Date unknown" to undated)
}

/** How many photos and videos, as the heading says it. */
fun countWords(files: List<CameraFile>): String {
    val photos = files.count { it.isPhoto }
    val videos = files.count { it.isVideo }
    return when {
        files.isEmpty() -> "Nothing on the card"
        else -> listOfNotNull(
            plural(videos, "video").takeIf { videos > 0 },
            plural(photos, "photo").takeIf { photos > 0 },
            plural(files.size - photos - videos, "other file").takeIf { files.size > photos + videos },
        ).joinToString(", ")
    }
}

private fun plural(count: Int, noun: String) = "$count $noun${if (count == 1) "" else "s"}"

/** What a file is, in a word. */
fun kindWord(kind: CameraFileKind): String = when (kind) {
    CameraFileKind.Photo -> "Photo"
    CameraFileKind.Video -> "Video"
    CameraFileKind.ProtectedVideo -> "Locked video"
    CameraFileKind.EmergencyVideo -> "Emergency video"
    CameraFileKind.Other -> "File"
}

/** A size as people read it: 295 KB, 9.1 MB, 2.3 GB. */
fun sizeWords(bytes: Long): String = when {
    bytes < MEGABYTE -> "${(bytes + 1023) / 1024} KB"
    bytes < GIGABYTE -> tenths(bytes, MEGABYTE) + " MB"
    else -> tenths(bytes, GIGABYTE) + " GB"
}

private fun tenths(bytes: Long, unit: Long): String {
    val tenths = (bytes * 10 + unit / 2) / unit
    return "${tenths / 10}.${tenths % 10}"
}

/** A file as a screen reader describes it. */
fun describe(file: CameraFile): String =
    listOfNotNull(
        kindWord(file.kind),
        file.taken?.let {
            SPOKEN.format(it)
        },
        sizeWords(file.approximateBytes),
    ).joinToString(", ")

private const val MEGABYTE = 1024L * 1024
private const val GIGABYTE = MEGABYTE * 1024
private val DAY: DateTimeFormatter = DateTimeFormatter.ofPattern("EEEE d MMMM yyyy", Locale.ENGLISH)
private val SPOKEN: DateTimeFormatter = DateTimeFormatter.ofPattern("d MMMM yyyy, HH:mm", Locale.ENGLISH)
