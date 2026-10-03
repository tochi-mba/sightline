package com.rextechnologies.sightline.core.settings

import com.rextechnologies.sightline.protocol.gpsock.MenuSetting
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import kotlin.test.Test
import kotlin.test.assertEquals

class CameraSettingGroupsTest {
    @Test
    fun `settings retain camera category order and order within each category`() {
        val settings = listOf(
            setting(1, "Resolution", "Record"),
            setting(2, "Exposure", "Record"),
            setting(3, "Language", "System"),
        )

        val grouped = settings.byCategory()

        assertEquals(listOf("Record", "System"), grouped.keys.toList())
        assertEquals(listOf("Resolution", "Exposure"), grouped.getValue("Record").map { it.name })
        assertEquals(emptyMap(), emptyList<MenuSetting>().byCategory())
    }

    private fun setting(id: Int, name: String, category: String) =
        MenuSetting(id, name, category, MenuSettingKind.Choice, 0, emptyList())
}
