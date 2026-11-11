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
    /** The computer's Windows volume (default playback device); null when it has none or is an older version. */
    val volume: Volume? = null,
    /** The display the volume belongs to (the one selected; its computer's sound). */
    val volumeFor: String? = null,
    /** The computer whose displays the "link brightness" button moves together ("" = all of them). */
    val linkOwner: String = "",
)

data class Volume(val percent: Int, val muted: Boolean)

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
    /** The display belongs to another paired computer (as that computer lists it; the phone keeps only each computer's own). */
    val remote: Boolean = false,
    /** The device id of the computer that owns the display ("" when the phone has one connection and no group). */
    val owner: String = "",
    /** The id the owner knows the display by (the phone's [id] adds the owner, so two computers never clash). */
    val key: String = "",
)

data class InputPort(val code: Int, val label: String, val current: Boolean, val inCycle: Boolean)

data class Profile(val name: String, val color: Long, val icon: String?, val active: Boolean, val custom: Boolean, val modified: Boolean = false)

data class Modes(val display: String, val current: Int?, val options: List<ModeOption>)

data class ModeOption(val value: Int, val label: String)

/** A program of a computer's Start menu (what the Remote tab opens and closes). */
data class AppItem(val id: String, val name: String, val running: Boolean = false)

/** The programs a computer offers; [allowed] is false until its owner turns on "Phone as remote". */
data class AppsList(val allowed: Boolean, val items: List<AppItem>)

/** A computer that answered the Wi-Fi search. */
data class Found(val name: String, val host: String, val port: Int, val deviceId: String = "")

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
            remote = d.optBoolean("remote"),
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
            modified = p.optBoolean("modified"),
        )
    },
    modes = o.optJSONObject("modes")?.let { m ->
        Modes(
            display = m.optString("display"),
            current = if (m.isNull("current")) null else m.optInt("current"),
            options = m.optJSONArray("options").objects().map { ModeOption(it.optInt("value"), it.optString("label")) },
        )
    },
    volume = o.optJSONObject("volume")?.let { Volume(it.optInt("percent"), it.optBoolean("muted")) },
    volumeFor = if (o.isNull("volumeFor")) null else o.optString("volumeFor").ifBlank { null },
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
