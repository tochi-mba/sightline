package com.rextechnologies.sightline.core.settings

import com.rextechnologies.sightline.core.camera.CameraSetting
import com.rextechnologies.sightline.protocol.gpsock.MenuSetting
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import kotlin.test.Test
import kotlin.test.assertEquals

class CameraSettingGroupsTest {
    @Test
    fun `settings keep the camera's category order and its order within each category`() {
        val settings = listOf(
            setting(1, "Resolution", "Record"),
            setting(2, "Exposure", "Record"),
            setting(3, "Language", "System"),
        )

        val grouped = settings.byCategory()

        assertEquals(listOf("Record", "System"), grouped.keys.toList())
        assertEquals(listOf("Resolution", "Exposure"), grouped.getValue("Record").map { it.menu.name })
        assertEquals(emptyMap(), emptyList<CameraSetting>().byCategory())
    }

    private fun setting(id: Int, name: String, category: String) =
        CameraSetting(MenuSetting(id, name, category, MenuSettingKind.Choice, 0, emptyList()), value = 0, text = null)
}
