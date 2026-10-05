package com.rextechnologies.sightline

import android.content.Context

/** What this build is, read from the installed package rather than a generated class. */
object BuildInfo {
    /** The version the build was given from VERSION, or a placeholder when the system will not say. */
    fun versionName(context: Context): String =
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: UNKNOWN

    /** What is reported when the version cannot be read: numbers no release ever has. */
    const val UNKNOWN = "0.0.0"
}
