package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.data.Display
import com.greypaulino.switchmonitor.data.InputPort
import com.greypaulino.switchmonitor.data.PcState
import com.greypaulino.switchmonitor.data.Profile
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.data.RemoteUi
import com.greypaulino.switchmonitor.ui.theme.Card
import com.greypaulino.switchmonitor.ui.theme.Green
import com.greypaulino.switchmonitor.ui.theme.Ground
import com.greypaulino.switchmonitor.ui.theme.Ink
import com.greypaulino.switchmonitor.ui.theme.Line
import com.greypaulino.switchmonitor.ui.theme.Muted
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.Soft
import com.greypaulino.switchmonitor.ui.theme.Text
import com.greypaulino.switchmonitor.ui.theme.Track
import com.greypaulino.switchmonitor.ui.theme.Warn
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

/**
 * The control screen. While (re)connecting it is already shown: with the last state the computer sent (dimmed) or, the
 * very first time, with placeholders the size of the panels, so nothing jumps when the computer answers.
 */
@Composable
fun ControlScreen(ui: RemoteUi, state: PcState?, live: Boolean = true) {
    val dim by androidx.compose.animation.core.animateFloatAsState(if (live) 1f else 0.55f, label = "dim")
    // The app is the whole group's, not one computer's: its name is "My devices", and the mouse has its own tab.
    val title = if (ui.inGroup) "My devices" else state?.computer?.ifBlank { null } ?: ui.computer ?: ui.host ?: "Computer"
    Column(Modifier.fillMaxSize().background(Ground).safeDrawingPadding()) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            if (controlTab == 0) {
                Column(
                    Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .padding(horizontal = 16.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(14.dp),
                ) {
                    Header(title, if (ui.inGroup) null else ui.host, ui.phase)
                    if (state == null) SkeletonPanels()
                    else Column(Modifier.graphicsLayer { alpha = dim }, verticalArrangement = Arrangement.spacedBy(14.dp)) {
                        BrightnessPanel(state)
                        // Volume follows the selected display, like the picture modes do. While (re)connecting: a placeholder the
                        // same height so nothing below moves.
                        if (state.volume != null) VolumePanel(state.volume, state.volumeFor) else if (!live) VolumeBone()
                        ProfilesPanel(state)
                        for (display in state.displays.filter { it.on && it.inputs.isNotEmpty() }) InputsPanel(display)
                    }
                    Spacer(Modifier.height(8.dp))
                }
            } else {
                TouchpadScreen(ui)
            }
            ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
        }
        TabBar()
    }
}

/** 0 = the controls, 1 = the mouse and keyboard. Kept outside the screen so it survives a reconnection. */
private var controlTab by mutableIntStateOf(0)

@Composable
private fun TabBar() {
    Row(
        Modifier.fillMaxWidth().background(Ground).border(1.dp, Line).padding(horizontal = 16.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        for ((i, label) in listOf("Controls", "Mouse").withIndex()) {
            val on = controlTab == i
            Box(
                Modifier.weight(1f).height(44.dp).clip(RoundedCornerShape(14.dp))
                    .background(if (on) Raised else Color.Transparent)
                    .clickable { controlTab = i },
                contentAlignment = Alignment.Center,
            ) { Text(label, fontSize = 15.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, color = if (on) Text else Muted) }
        }
    }
}

@Composable
private fun Header(computer: String, host: String?, phase: com.greypaulino.switchmonitor.data.Phase) {
    var menu by remember { mutableStateOf(false) }
    var pickWidget by remember { mutableStateOf(false) }
    val context = androidx.compose.ui.platform.LocalContext.current
    if (pickWidget) {
        val kinds = com.greypaulino.switchmonitor.surfaces.Widgets.Kind.entries
        val names = mapOf(
            com.greypaulino.switchmonitor.surfaces.Widgets.Kind.Large to "Brightness and profiles (4×2)",
            com.greypaulino.switchmonitor.surfaces.Widgets.Kind.Small to "Brightness with − and + (2×2)",
            com.greypaulino.switchmonitor.surfaces.Widgets.Kind.Profiles to "Profiles (2×2)",
        )
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { pickWidget = false },
            containerColor = Raised,
            title = { Text("Add a widget", color = Text) },
            text = {
                // Each widget drawn for real (same views the home screen gets); tap one to add it.
                Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    for (k in kinds) {
                        val square = k == com.greypaulino.switchmonitor.surfaces.Widgets.Kind.Small ||
                            k == com.greypaulino.switchmonitor.surfaces.Widgets.Kind.Profiles
                        Column(
                            Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp))
                                .clickable { pickWidget = false; com.greypaulino.switchmonitor.surfaces.Widgets.pin(context, k) }
                                .padding(6.dp),
                        ) {
                            Text(names[k] ?: k.name, Modifier.padding(bottom = 8.dp), color = Soft, fontSize = 13.sp)
                            Box(
                                if (square) Modifier.size(150.dp)
                                else Modifier.fillMaxWidth().height(170.dp),
                            ) {
                                androidx.compose.ui.viewinterop.AndroidView(
                                    factory = { ctx ->
                                        android.widget.FrameLayout(ctx).also { frame ->
                                            frame.addView(com.greypaulino.switchmonitor.surfaces.Widgets.build(ctx, k).apply(ctx, frame))
                                        }
                                    },
                                    modifier = Modifier.matchParentSize(),
                                )
                                // Covers the widget so a tap adds it instead of pressing its buttons.
                                Box(Modifier.matchParentSize().clickable { pickWidget = false; com.greypaulino.switchmonitor.surfaces.Widgets.pin(context, k) })
                            }
                        }
                    }
                }
            },
            confirmButton = { androidx.compose.material3.TextButton(onClick = { pickWidget = false }) { Text("Cancel", color = Soft) } },
        )
    }
    Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(40.dp).clip(RoundedCornerShape(12.dp)).background(Raised), contentAlignment = Alignment.Center) {
            Icon(Glyphs.Monitor, null, Modifier.size(20.dp), tint = Text)
        }
        Column(Modifier.weight(1f).padding(start = 12.dp)) {
            Text(computer, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Text, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically) {
                val (dot, status) = when (phase) {
                    com.greypaulino.switchmonitor.data.Phase.Connected -> Green to "Connected over Wi-Fi"
                    com.greypaulino.switchmonitor.data.Phase.Offline -> Warn to "Can't reach it · trying again"
                    else -> Muted to "Connecting…"
                }
                Box(Modifier.size(7.dp).clip(CircleShape).background(dot))
                Text(status, Modifier.padding(start = 6.dp), fontSize = 13.sp, color = Muted)
            }
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(Glyphs.More, "Menu", tint = Text) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }, containerColor = Raised) {
                if (host != null) DropdownMenuItem(text = { Text("Address: $host", color = Muted) }, onClick = { menu = false }, enabled = false)
                DropdownMenuItem(text = { Text("Add a widget…") }, onClick = { menu = false; pickWidget = true })
                DropdownMenuItem(text = { Text("Use another computer") }, onClick = { menu = false; Remote.forget() })
            }
        }
    }
}

// ---- Brightness ----

@Composable
private fun BrightnessPanel(state: PcState) {
    Panel(padding = androidx.compose.foundation.layout.PaddingValues(start = 10.dp, end = 10.dp, top = 12.dp, bottom = 8.dp)) {
        Row(Modifier.fillMaxWidth().padding(start = 6.dp, end = 4.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Glyphs.Sun, null, Modifier.size(20.dp), tint = Text)
            PanelTitle("Brightness", Modifier.weight(1f).padding(start = 10.dp))
            if (state.canLink) LinkButton(state.linked)
        }
        val shown = state.displays.filter { it.on }
        if (shown.isEmpty()) Text("No display can be controlled right now.", Modifier.padding(8.dp), color = Muted, fontSize = 14.sp)
        if (state.linked && shown.size > 1) LinkedBox(shown)
        else for (display in shown) DisplayRow(display)
    }
}

@Composable
internal fun LinkButton(linked: Boolean) {
    // Icon only (green when the displays move together).
    Box(
        Modifier
            .size(38.dp)
            .clip(CircleShape)
            .background(if (linked) Green else Raised)
            .border(1.dp, if (linked) Green else Line, CircleShape)
            .clickable(onClickLabel = if (linked) "Unlink brightness" else "Link brightness") { Remote.link(!linked) },
        contentAlignment = Alignment.Center,
    ) {
        Icon(Glyphs.Link, if (linked) "Linked" else "Not linked", Modifier.size(18.dp), tint = if (linked) Color(0xFF0F2416) else Text)
    }
}

/** One display with its own slider (not linked). */
@Composable
private fun DisplayRow(display: Display) {
    val level = remember(display.id) { mutableFloatStateOf((display.brightness ?: 0).toFloat()) }
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(if (display.selected) Raised else Color.Transparent)
            .clickable(enabled = !display.selected) { Remote.select(display.id) }
            .padding(start = 8.dp, end = 8.dp, top = 12.dp, bottom = 6.dp),
    ) {
        DisplayLine(display, lit = display.selected, value = if (display.ready) "${level.floatValue.roundToInt()}%" else "…")
        if (display.ready) BrightnessSlider(display, level)
        else SliderBone()
    }
}

/** Linked, like the desktop panel: every display's name (green) in one box and a single slider they all follow. */
@Composable
private fun LinkedBox(displays: List<Display>) {
    val lead = displays.firstOrNull { it.ready } ?: displays.first()
    val level = remember(lead.id) { mutableFloatStateOf((lead.brightness ?: 0).toFloat()) }
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(Raised)
            .padding(start = 8.dp, end = 8.dp, top = 10.dp, bottom = 6.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        displays.forEachIndexed { i, d ->
            DisplayLine(d, lit = true, value = if (i == 0) (if (lead.ready) "${level.floatValue.roundToInt()}%" else "…") else "")
        }
        if (lead.ready) BrightnessSlider(lead, level) else SliderBone()
    }
}

/** The first connection ever (nothing kept yet): the panels drawn as placeholders. */
@Composable
private fun SkeletonPanels() {
    Panel(padding = androidx.compose.foundation.layout.PaddingValues(start = 10.dp, end = 10.dp, top = 12.dp, bottom = 8.dp)) {
        Row(Modifier.fillMaxWidth().padding(start = 6.dp, end = 4.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Glyphs.Sun, null, Modifier.size(20.dp), tint = Text)
            PanelTitle("Brightness", Modifier.weight(1f).padding(start = 10.dp))
            Bone(Modifier.size(38.dp), CircleShape)
        }
        repeat(2) {
            Column(Modifier.fillMaxWidth().padding(start = 8.dp, end = 8.dp, top = 12.dp, bottom = 6.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Bone(Modifier.size(8.dp), CircleShape)
                    Bone(Modifier.padding(start = 10.dp).size(18.dp))
                    Bone(Modifier.padding(start = 10.dp).width(if (it == 0) 170.dp else 130.dp).height(14.dp))
                    Spacer(Modifier.weight(1f))
                    Bone(Modifier.width(36.dp).height(14.dp))
                }
                SliderBone()
            }
        }
    }
    Panel {
        PanelTitle("Profiles", Modifier.padding(bottom = 12.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            repeat(5) {
                Column(Modifier.width(56.dp).padding(vertical = 2.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Bone(Modifier.size(46.dp).padding(4.dp), CircleShape)
                    Bone(Modifier.padding(top = 7.dp).width(36.dp).height(11.dp))
                }
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun DisplayLine(display: Display, lit: Boolean, value: String) {
    val haptics = LocalHapticFeedback.current
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).clip(CircleShape).background(if (lit) Green else Color(0xFF5A5A5A)))
        Icon(if (display.isLaptop) Glyphs.Laptop else Glyphs.Monitor, null, Modifier.padding(start = 10.dp).size(18.dp), tint = Muted)
        Text(display.name, Modifier.weight(1f).padding(start = 10.dp), fontSize = 15.sp, fontWeight = FontWeight.Medium, color = Text,
            maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (display.main || display.canBeMain) {
            val on = display.main
            Box(
                Modifier
                    .height(30.dp)
                    .widthIn(min = 34.dp)
                    .clip(RoundedCornerShape(15.dp))
                    .background(if (on) Text else Color.Transparent)
                    .border(1.dp, if (on) Text else Color(0x33FFFFFF), RoundedCornerShape(15.dp))
                    .combinedClickable(
                        onClick = { if (!on) Remote.main(display.id) },
                        onLongClick = { haptics.performHapticFeedback(HapticFeedbackType.LongPress); Remote.preferred(display.id) },
                        onClickLabel = "Make main display", onLongClickLabel = "Preferred main display",
                    )
                    .padding(horizontal = 10.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(if (display.preferred) "M ★" else "M", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = if (on) Ink else Muted)
            }
        }
        Text(value, Modifier.width(48.dp), textAlign = TextAlign.End, fontSize = 15.sp, color = Soft)
    }
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun BrightnessSlider(display: Display, level: androidx.compose.runtime.MutableFloatState) {
    val scope = rememberCoroutineScope()
    // While a finger is on the slider the computer's echoes don't move it; it follows them again shortly after release.
    var dragging by remember { mutableStateOf(false) }
    LaunchedEffect(display.brightness, dragging) { if (!dragging) display.brightness?.let { level.floatValue = it.toFloat() } }
    Slider(
        value = level.floatValue,
        onValueChange = {
            dragging = true
            level.floatValue = it
            Remote.brightness(display.id, it.roundToInt())
        },
        onValueChangeFinished = { scope.launch { delay(600); dragging = false } },
        valueRange = 0f..100f,
        // The desktop panel's slider: a thin white fill on a grey track and a small round white thumb.
        thumb = { Box(Modifier.size(16.dp).shadow(2.dp, CircleShape).background(Text, CircleShape)) },
        track = { slider ->
            val fraction = (slider.value / 100f).coerceIn(0f, 1f)
            Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(Track)) {
                Box(Modifier.fillMaxWidth(fraction).fillMaxHeight().background(Text))
            }
        },
        modifier = Modifier.fillMaxWidth().height(36.dp),
    )
}

// ---- Windows volume (folded away until opened) ----

@Composable
private fun VolumeBone() {
    Panel(padding = androidx.compose.foundation.layout.PaddingValues(start = 10.dp, end = 10.dp, top = 6.dp, bottom = 6.dp)) {
        Row(Modifier.fillMaxWidth().padding(start = 6.dp, end = 4.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Bone(Modifier.size(20.dp))
            Bone(Modifier.padding(start = 10.dp).width(70.dp).height(16.dp))
            Spacer(Modifier.weight(1f))
            Bone(Modifier.width(36.dp).height(14.dp))
        }
    }
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun VolumePanel(volume: com.greypaulino.switchmonitor.data.Volume, forDisplay: String? = null) {
    var open by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf(false) }
    val level = remember { mutableFloatStateOf(volume.percent.toFloat()) }
    val scope = rememberCoroutineScope()
    var dragging by remember { mutableStateOf(false) }
    LaunchedEffect(volume.percent, dragging) { if (!dragging) level.floatValue = volume.percent.toFloat() }
    val turn by androidx.compose.animation.core.animateFloatAsState(if (open) 0f else -90f, label = "chevron")
    Panel(padding = androidx.compose.foundation.layout.PaddingValues(start = 10.dp, end = 10.dp, top = 6.dp, bottom = 6.dp)) {
        Row(
            Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(14.dp))
                .clickable(onClickLabel = if (open) "Hide volume" else "Show volume") { open = !open }
                .padding(start = 6.dp, end = 4.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(if (volume.muted) Glyphs.SpeakerOff else Glyphs.Speaker, null, Modifier.size(20.dp), tint = Text)
            Column(Modifier.weight(1f).padding(start = 10.dp)) {
                PanelTitle("Volume")
                if (forDisplay != null) Text(forDisplay, fontSize = 12.sp, color = Muted, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Text(if (volume.muted) "Muted" else "${level.floatValue.roundToInt()}%", Modifier.padding(end = 8.dp), fontSize = 15.sp, color = Soft)
            Icon(Glyphs.ChevronDown, null, Modifier.size(18.dp).graphicsLayer { rotationZ = turn }, tint = Muted)
        }
        androidx.compose.animation.AnimatedVisibility(open) {
            Row(Modifier.fillMaxWidth().padding(start = 2.dp, end = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(38.dp)
                        .clip(CircleShape)
                        .background(if (volume.muted) Text else Raised)
                        .border(1.dp, if (volume.muted) Text else Line, CircleShape)
                        .clickable(onClickLabel = if (volume.muted) "Turn the sound on" else "Mute") { Remote.mute(!volume.muted) },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(if (volume.muted) Glyphs.SpeakerOff else Glyphs.Speaker, if (volume.muted) "Muted" else "Sound on",
                        Modifier.size(18.dp), tint = if (volume.muted) Ink else Text)
                }
                Slider(
                    value = level.floatValue,
                    onValueChange = {
                        dragging = true
                        level.floatValue = it
                        Remote.volume(it.roundToInt())
                    },
                    onValueChangeFinished = { scope.launch { delay(600); dragging = false } },
                    valueRange = 0f..100f,
                    thumb = { Box(Modifier.size(16.dp).shadow(2.dp, CircleShape).background(Text, CircleShape)) },
                    track = { slider ->
                        val fraction = (slider.value / 100f).coerceIn(0f, 1f)
                        Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(Track)) {
                            Box(Modifier.fillMaxWidth(fraction).fillMaxHeight().background(if (volume.muted) Muted else Text))
                        }
                    },
                    modifier = Modifier.weight(1f).padding(start = 12.dp).height(36.dp),
                )
            }
        }
    }
}

// ---- Profiles and picture modes ----

@Composable
private fun ProfilesPanel(state: PcState) {
    if (state.profiles.isEmpty() && state.modes == null) return
    Panel {
        PanelTitle("Profiles", Modifier.padding(bottom = 12.dp))
        FadingRow(spacing = 6.dp, selected = state.profiles.indexOfFirst { it.active }) {
            items(state.profiles, key = { it.name }) { ProfileBadge(it) }
        }
        state.modes?.let { modes ->
            HorizontalDivider(Modifier.padding(top = 16.dp, bottom = 14.dp), color = Color(0x1AFFFFFF))
            Text("Picture mode · ${modes.display}", Modifier.padding(bottom = 10.dp), fontSize = 13.sp, color = Muted)
            FadingRow(spacing = 8.dp, selected = modes.options.indexOfFirst { it.value == modes.current }) {
                items(modes.options, key = { it.value }) { option ->
                    val on = option.value == modes.current
                    Box(
                        Modifier
                            .height(38.dp)
                            .clip(RoundedCornerShape(19.dp))
                            .background(if (on) Text else Raised)
                            .border(1.dp, if (on) Text else Line, RoundedCornerShape(19.dp))
                            .clickable(enabled = !on) { Remote.mode(option.value) }
                            .padding(start = 12.dp, end = 14.dp),
                        contentAlignment = Alignment.Center,
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Glyphs.pictureMode(option.label), null, Modifier.size(16.dp), tint = if (on) Ink else Soft)
                            Text(option.label, Modifier.padding(start = 6.dp), fontSize = 13.sp, color = if (on) Ink else Text,
                                fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, maxLines = 1)
                        }
                    }
                }
            }
        }
    }
}

@Composable
internal fun ProfileBadge(profile: Profile) {
    Column(
        Modifier
            .width(56.dp)
            .clip(RoundedCornerShape(12.dp))
            .clickable { Remote.profile(profile.name) }
            .padding(vertical = 2.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(
            Modifier
                .size(46.dp)
                .then(if (profile.active) Modifier.border(2.dp, Text, CircleShape) else Modifier)
                .padding(4.dp)
                .clip(CircleShape)
                .background(Color(profile.color)),
            contentAlignment = Alignment.Center,
        ) {
            val glyph = Glyphs.profile(profile.icon, profile.custom)
            if (glyph != null) Icon(glyph, null, Modifier.size(18.dp), tint = Ink)
            else Text(profile.name.take(1).uppercase(), fontSize = 16.sp, fontWeight = FontWeight.Bold, color = Ink)
        }
        Text(profile.name, Modifier.padding(top = 5.dp), fontSize = 11.sp, color = Soft, maxLines = 1, overflow = TextOverflow.Ellipsis)
        // Changed by hand since it was applied: it stays chosen, marked "custom" (the computer offers to save it).
        if (profile.modified) Text("custom", fontSize = 10.sp, color = Muted, maxLines = 1)
    }
}

// ---- Inputs ----

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun InputsPanel(display: Display) {
    val haptics = LocalHapticFeedback.current
    var armed by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(armed) { if (armed != null) { delay(3000); armed = null } }
    Panel {
        PanelTitle("Input · ${display.name}", Modifier.padding(bottom = 12.dp))
        for (row in display.inputs.chunked(3)) {
            Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (port in row) InputTile(port, armed == port.code, Modifier.weight(1f),
                    onTap = {
                        if (!port.current) {
                            if (armed == port.code) { armed = null; Remote.input(display.id, port.code) } else armed = port.code
                        }
                    },
                    onHold = { haptics.performHapticFeedback(HapticFeedbackType.LongPress); Remote.cycle(display.id, port.code, !port.inCycle) })
                repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
        Text("Tap twice to switch · hold to add or skip it in Next input", fontSize = 12.sp, color = Muted)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun InputTile(port: InputPort, armed: Boolean, modifier: Modifier, onTap: () -> Unit, onHold: () -> Unit) {
    val back = when { port.current -> Text; armed -> Warn; else -> Raised }
    val fore = if (port.current || armed) Ink else Text
    Box(
        modifier
            .heightIn(min = 96.dp)
            .clip(RoundedCornerShape(16.dp))
            .background(back)
            .border(1.dp, if (port.current || armed) back else Line, RoundedCornerShape(16.dp))
            .combinedClickable(onClick = onTap, onLongClick = onHold),
    ) {
        // Next input: a green badge when * stops at this input, a hollow one when it skips it (hold the tile to change).
        Box(
            Modifier
                .padding(7.dp)
                .size(20.dp)
                .clip(CircleShape)
                .background(if (port.inCycle) Green else Color.Transparent)
                .border(1.5.dp, if (port.inCycle) Green else fore.copy(alpha = 0.35f), CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Glyphs.Next, if (port.inCycle) "In Next input" else "Skipped by Next input", Modifier.size(12.dp),
                tint = if (port.inCycle) Ink else fore.copy(alpha = 0.45f))
        }
        Column(
            Modifier.matchParentSize().padding(start = 6.dp, end = 6.dp, top = 14.dp, bottom = 6.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Icon(Glyphs.Port, null, Modifier.size(24.dp), tint = fore)
            Text(port.label, Modifier.padding(top = 4.dp), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = fore,
                maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (port.current || armed) Text(if (port.current) "In use" else "Tap again", fontSize = 11.sp, color = fore.copy(alpha = 0.8f))
        }
    }
}

@Composable
fun Toast(text: String, modifier: Modifier = Modifier) {
    Box(
        modifier
            .safeDrawingPadding()
            .padding(16.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(Card)
            .border(1.dp, Warn.copy(alpha = 0.5f), RoundedCornerShape(14.dp))
            .padding(horizontal = 16.dp, vertical = 12.dp),
    ) {
        Text(text, color = Warn, fontSize = 14.sp)
    }
}
