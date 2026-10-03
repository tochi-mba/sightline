package com.rextechnologies.sightline.core.settings

import com.rextechnologies.sightline.protocol.gpsock.MenuSetting

/** Groups the camera's own catalogue without inventing settings the firmware did not advertise. */
fun List<MenuSetting>.byCategory(): Map<String, List<MenuSetting>> =
    groupByTo(linkedMapOf()) { setting -> setting.category }
