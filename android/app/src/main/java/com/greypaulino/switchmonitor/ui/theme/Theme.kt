package com.greypaulino.switchmonitor.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable

private val Scheme = darkColorScheme(
    primary = Text,
    onPrimary = Ink,
    secondary = Green,
    background = Ground,
    onBackground = Text,
    surface = Card,
    onSurface = Text,
    surfaceVariant = Raised,
    onSurfaceVariant = Muted,
    outline = Line,
    error = Warn,
)

@Composable
fun SwitchMonitorTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = Scheme, typography = AppTypography, content = content)
}
