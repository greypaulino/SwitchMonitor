package com.greypaulino.switchmonitor.data

import org.json.JSONArray
import org.json.JSONObject

/** What the computer shares with the phone (SwitchMonitor > Settings > Phone; see PhoneBridge.BuildState on the PC). */
data class PcState(
    val computer: String,
    val linked: Boolean,
    val canLink: Boolean,
    val displays: List<Display>,
    val profiles: List<Profile>,
    val modes: Modes?,
)

data class Display(
    val id: String,
    val name: String,
    val isLaptop: Boolean,
    val on: Boolean,
    val ready: Boolean,
    val brightness: Int?,
    val selected: Boolean,
    val main: Boolean,
    val preferred: Boolean,
    val canBeMain: Boolean,
    val inputs: List<InputPort>,
)

data class InputPort(val code: Int, val label: String, val current: Boolean, val inCycle: Boolean)

data class Profile(val name: String, val color: Long, val icon: String?, val active: Boolean, val custom: Boolean)

data class Modes(val display: String, val current: Int?, val options: List<ModeOption>)

data class ModeOption(val value: Int, val label: String)

/** A computer that answered the Wi-Fi search. */
data class Found(val name: String, val host: String, val port: Int)

internal fun parseState(o: JSONObject): PcState = PcState(
    computer = o.optString("computer"),
    linked = o.optBoolean("linked"),
    canLink = o.optBoolean("canLink"),
    displays = o.optJSONArray("displays").objects().map { d ->
        Display(
            id = d.optString("id"),
            name = d.optString("name"),
            isLaptop = d.optString("kind") == "laptop",
            on = d.optBoolean("on"),
            ready = d.optBoolean("ready"),
            brightness = if (d.isNull("brightness")) null else d.optInt("brightness"),
            selected = d.optBoolean("selected"),
            main = d.optBoolean("main"),
            preferred = d.optBoolean("preferred"),
            canBeMain = d.optBoolean("canBeMain"),
            inputs = d.optJSONArray("inputs").objects().map { i ->
                InputPort(i.optInt("code"), i.optString("label"), i.optBoolean("current"), i.optBoolean("inCycle"))
            },
        )
    },
    profiles = o.optJSONArray("profiles").objects().map { p ->
        Profile(
            name = p.optString("name"),
            color = parseColor(p.optString("color")),
            icon = if (p.isNull("icon")) null else p.optString("icon"),
            active = p.optBoolean("active"),
            custom = p.optBoolean("custom"),
        )
    },
    modes = o.optJSONObject("modes")?.let { m ->
        Modes(
            display = m.optString("display"),
            current = if (m.isNull("current")) null else m.optInt("current"),
            options = m.optJSONArray("options").objects().map { ModeOption(it.optInt("value"), it.optString("label")) },
        )
    },
)

private fun JSONArray?.objects(): List<JSONObject> =
    if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }

/** "#RRGGBB" or "#AARRGGBB" as 0xAARRGGBB; grey when it can't be read. */
internal fun parseColor(text: String?): Long {
    val hex = text?.trim()?.removePrefix("#") ?: return 0xFFBDBDBD
    return try {
        when (hex.length) {
            6 -> 0xFF000000 or hex.toLong(16)
            8 -> hex.toLong(16)
            else -> 0xFFBDBDBD
        }
    } catch (_: NumberFormatException) {
        0xFFBDBDBD
    }
}
