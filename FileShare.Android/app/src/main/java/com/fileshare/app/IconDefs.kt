package com.fileshare.app

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp

// Canonical Material glyphs (24dp grid) that are not in material-icons-core.
// Paths transcribed from the standard Material icon set.

val PauseIcon: ImageVector = ImageVector.Builder(
    name = "PauseIcon",
    defaultWidth = 24.dp, defaultHeight = 24.dp,
    viewportWidth = 24f, viewportHeight = 24f,
).apply {
    path(fill = SolidColor(Color.Black)) {
        // M6 19h4V5H6v14zm8-14v14h4V5h-4z
        moveTo(6f, 19f); horizontalLineTo(10f); verticalLineTo(5f); horizontalLineTo(6f); close()
        moveTo(14f, 5f); horizontalLineTo(18f); verticalLineTo(19f); horizontalLineTo(14f); close()
    }
}.build()

val DescriptionIcon: ImageVector = ImageVector.Builder(
    name = "DescriptionIcon",
    defaultWidth = 24.dp, defaultHeight = 24.dp,
    viewportWidth = 24f, viewportHeight = 24f,
).apply {
    path(fill = SolidColor(Color.Black)) {
        // M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6z
        moveTo(14f, 2f); horizontalLineTo(6f)
        curveToRelative(-1.1f, 0f, -1.99f, 0.9f, -1.99f, 2f)
        lineTo(4f, 20f)
        curveToRelative(0f, 1.1f, 0.89f, 2f, 1.99f, 2f)
        horizontalLineTo(18f)
        curveToRelative(1.1f, 0f, 2f, -0.9f, 2f, -2f)
        verticalLineTo(8f)
        lineToRelative(-6f, -6f)
        close()
        // two text rules + folded corner
        moveTo(16f, 18f); horizontalLineTo(8f); verticalLineTo(16f); horizontalLineTo(16f); close()
        moveTo(16f, 14f); horizontalLineTo(8f); verticalLineTo(12f); horizontalLineTo(16f); close()
        moveTo(13f, 9f); verticalLineTo(3.5f); lineTo(18.5f, 9f); horizontalLineTo(13f); close()
    }
}.build()
