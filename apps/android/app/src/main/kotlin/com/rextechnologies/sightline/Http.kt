package com.rextechnologies.sightline

import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

/**
 * Fetches [address] as text over the phone's ordinary internet connection, never the camera's: the app's
 * process is not bound to the camera network, so this takes the default route like any app's request.
 */
fun httpGet(address: String): String {
    val connection = URL(address).openConnection() as HttpURLConnection
    try {
        connection.connectTimeout = TIMEOUT_MILLIS
        connection.readTimeout = TIMEOUT_MILLIS
        connection.setRequestProperty("Accept", "application/vnd.github+json")
        connection.setRequestProperty("User-Agent", "Sightline")
        // Disconnecting, below, closes the stream whether or not the read finished.
        return InputStreamReader(connection.inputStream, Charsets.UTF_8).readText()
    } finally {
        connection.disconnect()
    }
}

private const val TIMEOUT_MILLIS = 10_000
