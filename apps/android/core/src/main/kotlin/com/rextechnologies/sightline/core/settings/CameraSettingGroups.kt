package com.rextechnologies.sightline.core.settings

import com.rextechnologies.sightline.core.camera.CameraSetting

/**
 * The camera's settings grouped the way its own menu groups them, in its order.
 *
 * Grouped from the camera's catalogue rather than a table in the app, so no setting the firmware did not
 * advertise is invented and none it did is left out.
 */
fun List<CameraSetting>.byCategory(): Map<String, List<CameraSetting>> = groupByTo(linkedMapOf()) { it.menu.category }
