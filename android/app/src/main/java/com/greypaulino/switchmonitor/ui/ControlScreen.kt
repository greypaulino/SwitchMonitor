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

@Composable
fun ControlScreen(ui: RemoteUi, state: PcState) {
    Box(Modifier.fillMaxSize().background(Ground)) {
        Column(
            Modifier
                .fillMaxSize()
                .safeDrawingPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Header(state.computer.ifBlank { ui.computer ?: "Computer" }, ui.host)
            BrightnessPanel(state)
            ProfilesPanel(state)
            for (display in state.displays.filter { it.on && it.inputs.isNotEmpty() }) InputsPanel(display)
            Spacer(Modifier.height(8.dp))
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

@Composable
private fun Header(computer: String, host: String?) {
    var menu by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(40.dp).clip(RoundedCornerShape(12.dp)).background(Raised), contentAlignment = Alignment.Center) {
            Icon(Glyphs.Monitor, null, Modifier.size(20.dp), tint = Text)
        }
        Column(Modifier.weight(1f).padding(start = 12.dp)) {
            Text(computer, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Text, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(7.dp).clip(CircleShape).background(Green))
                Text("Connected over Wi-Fi", Modifier.padding(start = 6.dp), fontSize = 13.sp, color = Muted)
            }
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(Glyphs.More, "Menu", tint = Text) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }, containerColor = Raised) {
                if (host != null) DropdownMenuItem(text = { Text("Address: $host", color = Muted) }, onClick = { menu = false }, enabled = false)
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
private fun LinkButton(linked: Boolean) {
    Row(
        Modifier
            .height(36.dp)
            .clip(RoundedCornerShape(18.dp))
            .background(if (linked) Green else Raised)
            .border(1.dp, if (linked) Green else Line, RoundedCornerShape(18.dp))
            .clickable { Remote.link(!linked) }
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Glyphs.Link, null, Modifier.size(16.dp), tint = if (linked) Color(0xFF0F2416) else Text)
        Text("Linked", Modifier.padding(start = 6.dp), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = if (linked) Color(0xFF0F2416) else Text)
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
        else Text("Reading the brightness…", Modifier.padding(top = 10.dp, bottom = 8.dp), fontSize = 13.sp, color = Muted)
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
        if (lead.ready) BrightnessSlider(lead, level)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun DisplayLine(display: Display, lit: Boolean, value: String) {
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
private fun ProfileBadge(profile: Profile) {
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
