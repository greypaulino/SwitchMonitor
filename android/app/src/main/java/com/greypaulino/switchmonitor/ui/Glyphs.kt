package com.greypaulino.switchmonitor.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/** Line icons (24×24, round 2px strokes), drawn like the desktop app's Line style. Tint them with Icon(tint = …). */
object Glyphs {
    private fun line(name: String, vararg paths: String, width: Float = 2f): ImageVector =
        ImageVector.Builder(name, 24.dp, 24.dp, 24f, 24f).apply {
            for (p in paths) addPath(
                pathData = addPathNodes(p), fill = null, stroke = SolidColor(Color.White), strokeLineWidth = width,
                strokeLineCap = StrokeCap.Round, strokeLineJoin = StrokeJoin.Round,
            )
        }.build()

    private fun circle(cx: Float, cy: Float, r: Float) = "M${cx - r} ${cy}a$r $r 0 1 0 ${2 * r} 0a$r $r 0 1 0 ${-2 * r} 0"

    val Monitor = line("monitor", "M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z", "M8 20h8M12 16v4")
    val Laptop = line("laptop", "M5.5 5h13a1.5 1.5 0 0 1 1.5 1.5v8a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 14.5v-8A1.5 1.5 0 0 1 5.5 5z", "M2 19h20")
    val Sun = line("sun", circle(12f, 12f, 4f), "M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4")
    val Link = line("link", "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7", "M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7")
    val More = line("more", "M12 5h.01M12 12h.01M12 19h.01", width = 3f)
    val ChevronRight = line("chevron", "M9 5l7 7-7 7", width = 2.4f)
    val ChevronDown = line("chevron-down", "M6 9l6 6 6-6", width = 2.4f)
    val Speaker = line("speaker", "M4 9.5h3.5L12 5.5v13l-4.5-4H4z", "M15.5 9a4.5 4.5 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11")
    val SpeakerOff = line("speaker-off", "M4 9.5h3.5L12 5.5v13l-4.5-4H4z", "M16 9.5l5 5M21 9.5l-5 5")
    val Wifi = line("wifi", "M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M2 9a15 15 0 0 1 20 0", circle(12f, 19.5f, 1f))
    val Check = line("check", "M5 12.5l4.5 4.5L19 7.5", width = 2.2f)
    val Port = line("port", "M2 7h20v6l-3 4H5l-3-4z", "M7 10h10", width = 1.8f)
    val Next = line("next", "M4 12h12M12 6l6 6-6 6", width = 2.6f)
    private val Edit = line("edit", "M4 20h4L19 9a2.8 2.8 0 0 0-4-4L4 16z", "M13.5 6.5l4 4")
    private val Sport = line("sport", "M13 4a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0", "M7 21l3-6 3 2v5M10 15l1-5 3 2 3 1M8 9l3-1")
    private val Tv = line("tv", "M4 6h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1z", "M8 21h8")

    /** A picture mode's icon by its name, as the desktop app picks it (Icons.ForValue). */
    fun pictureMode(label: String): ImageVector {
        val v = label.lowercase()
        return when {
            "game" in v || v.startsWith("fps") || v.startsWith("rts") -> profile("game", false)!!
            "custom" in v || "user" in v -> Edit
            "read" in v -> profile("reading", false)!!
            "photo" in v -> profile("photo", false)!!
            "cinema" in v || "movie" in v -> profile("movie", false)!!
            "hdr" in v -> profile("bulb", false)!!
            "weak" in v -> profile("eye", false)!!
            "productivity" in v -> profile("work", false)!!
            "sport" in v -> Sport
            else -> Tv
        }
    }

    val Power = line("power", "M12 3v8", "M17.7 6.3a8 8 0 1 1-11.4 0")
    val Mouse = line("mouse", "M12 3a5.5 5.5 0 0 1 5.5 5.5v7a5.5 5.5 0 0 1-11 0v-7A5.5 5.5 0 0 1 12 3z", "M12 3v6")
    val Remote = line("remote", circle(12f, 12f, 9f), circle(12f, 12f, 3f), "M12 3v3M12 18v3M3 12h3M18 12h3")
    val Widget = line("widget", "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4z", "M16.5 13v7M13 16.5h7")
    val Swap = line("swap", "M4 8h13M14 4l3 4-3 4", "M20 16H7M10 12l-3 4 3 4")
    val Copy = line("copy", "M9 9h11v11H9z", "M5 15V4h10")
    val Close = line("close", "M6 6l12 12M18 6L6 18", width = 2.2f)
    val Refresh = line("refresh", "M20 11a8 8 0 1 0-2.3 5.7", "M20 4v7h-7")

    // Profile icons, by the library ids the PC sends (Icons.ProfileIconId).
    private val Custom = line("custom", "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12", circle(16f, 6f, 2f), circle(10f, 12f, 2f), circle(18f, 18f, 2f))
    private val Work = line("work", "M5 7h14a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2z", "M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M3 13h18")
    private val Night = line("night", "M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z")
    private val Game = line("game", "M6 11h4M8 9v4M15 12h.01M18 10h.01", "M17.3 5H6.7a4 4 0 0 0-4 3.6L2 15a3 3 0 0 0 5.4 1.8L9 15h6l1.6 1.8A3 3 0 0 0 22 15l-.7-6.4a4 4 0 0 0-4-3.6z")
    private val Movie = line("movie", "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z", "M7 4v16M17 4v16M2 9h5M2 15h5M17 9h5M17 15h5")
    private val Photo = line("photo", "M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z", circle(12f, 13.5f, 3f))
    private val Reading = line("reading", "M2 5h6a4 4 0 0 1 4 4v11a3 3 0 0 0-3-3H2z", "M22 5h-6a4 4 0 0 0-4 4v11a3 3 0 0 1 3-3h7z")
    private val Star = line("star", "M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.3 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z")
    private val Home = line("home", "M3 11l9-8 9 8", "M5 9.5V20h14V9.5")
    private val Music = line("music", "M9 18V5l12-2v13", circle(6f, 18f, 3f), circle(18f, 16f, 3f))
    private val Bulb = line("bulb", "M9 18h6M10 21h4", "M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2V16h5v-.1c0-.8.4-1.5 1-2A6 6 0 0 0 12 3z")
    private val Eye = line("eye", "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z", circle(12f, 12f, 3f))
    private val Headphones = line("headphones", "M3 18v-6a9 9 0 0 1 18 0v6", "M21 19a2 2 0 0 1-2 2h-1v-6h3zM3 19a2 2 0 0 0 2 2h1v-6H3z")

    /** The icon of a profile, or null to show its initial. */
    fun profile(icon: String?, custom: Boolean): ImageVector? = when {
        custom -> Custom
        else -> when (icon) {
            "work" -> Work
            "night" -> Night
            "game" -> Game
            "movie", "video" -> Movie
            "photo", "picture" -> Photo
            "reading", "library", "document", "school" -> Reading
            "star" -> Star
            "home" -> Home
            "music", "sound" -> Music
            "headphones" -> Headphones
            "sun" -> Sun
            "bulb" -> Bulb
            "eye" -> Eye
            "monitor" -> Monitor
            "laptop" -> Laptop
            else -> null
        }
    }
}
