package com.tvos.remote

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/**
 * The handheld microphone the owner chose: a round head with a band across it and two grille dots, a collar,
 * a solid tapered handle, tilted, no cable. Drawn from the `#mic` symbol in the
 * design mockup of 2026-10-31 (the same 64 by 64 box, the
 * circles written as arcs). Black here; an Icon tints it.
 */
val MicIcon: ImageVector by lazy {
    val ink = SolidColor(Color.Black)
    ImageVector.Builder(
        name = "Mic",
        defaultWidth = 64.dp,
        defaultHeight = 64.dp,
        viewportWidth = 64f,
        viewportHeight = 64f,
    ).apply {
        addGroup(rotate = -40f, pivotX = 32f, pivotY = 32f)
        // Head: an outlined ball, then the band across it.
        addPath(
            addPathNodes("M22 14 A10 10 0 1 1 42 14 A10 10 0 1 1 22 14 Z"),
            stroke = ink,
            strokeLineWidth = 3.4f,
        )
        addPath(
            addPathNodes("M21 17.5 Q32 22.5 43 17.5"),
            stroke = ink,
            strokeLineWidth = 3.4f,
            strokeLineCap = StrokeCap.Round,
        )
        // Two grille dots.
        addPath(addPathNodes("M26.9 10.5 A1.6 1.6 0 1 1 30.1 10.5 A1.6 1.6 0 1 1 26.9 10.5 Z"), fill = ink)
        addPath(addPathNodes("M30.4 13 A1.6 1.6 0 1 1 33.6 13 A1.6 1.6 0 1 1 30.4 13 Z"), fill = ink)
        // Collar, then the solid handle with a rounded end.
        addPath(addPathNodes("M24.5 26 L39.5 26 L38.8 29.5 L25.2 29.5 Z"), fill = ink)
        addPath(
            addPathNodes("M25.2 32 L38.8 32 L36.6 55 Q32 61 27.4 55 Z"),
            fill = ink,
            stroke = ink,
            strokeLineWidth = 2f,
            strokeLineJoin = StrokeJoin.Round,
        )
        clearGroup()
    }.build()
}
