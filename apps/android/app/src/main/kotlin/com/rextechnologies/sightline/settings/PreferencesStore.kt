package com.rextechnologies.sightline.settings

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import com.rextechnologies.sightline.core.settings.SettingsStore

/**
 * The app's settings, kept in the phone's private preferences.
 *
 * Written with apply, which returns at once and saves in the background: a setting toggled on the
 * settings screen must not stall the frame that draws the toggle.
 */
class PreferencesStore(private val preferences: SharedPreferences) : SettingsStore {
    constructor(context: Context) : this(context.getSharedPreferences(FILE, Context.MODE_PRIVATE))

    override fun read(key: String): String? = try {
        preferences.getString(key, null)
    } catch (wrongType: ClassCastException) {
        // Something else wrote a number or a flag under this key; reading it as unset gives the
        // setting's default, which is what a value that cannot be read should give.
        null
    }

    override fun write(key: String, value: String?) {
        preferences.edit {
            if (value == null) remove(key) else putString(key, value)
        }
    }

    private companion object {
        const val FILE = "sightline"
    }
}
