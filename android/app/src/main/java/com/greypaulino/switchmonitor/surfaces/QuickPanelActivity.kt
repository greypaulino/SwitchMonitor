package com.greypaulino.switchmonitor.surfaces

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.data.Display
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.ui.DisplayLine
import com.greypaulino.switchmonitor.ui.FadingRow
import com.greypaulino.switchmonitor.ui.Glyphs
import com.greypaulino.switchmonitor.ui.ProfileBadge
import androidx.compose.foundation.lazy.items
import com.greypaulino.switchmonitor.ui.theme.Card
import com.greypaulino.switchmonitor.ui.theme.Line
import com.greypaulino.switchmonitor.ui.theme.Muted
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.SwitchMonitorTheme
import com.greypaulino.switchmonitor.ui.theme.Text
import com.greypaulino.switchmonitor.ui.theme.Track
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject
import kotlin.math.roundToInt
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.foundation.layout.width
import com.greypaulino.switchmonitor.ui.Bone
import com.greypaulino.switchmonitor.ui.SliderBone

/**
 * The quick panel: opened by the Quick Settings tile or by the percentage of a widget. A sheet over whatever is on
 * screen: each display (M for main, hold for preferred), Linked, a slider per display (one when linked), and the
 * profiles. Tapping outside closes it. It opens without the system's slow window animation: the sheet itself rises in
 * about 0.15 s.
 */
class QuickPanelActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        noWindowAnimation(opening = true)
        Remote.init(this)
        setContent { SwitchMonitorTheme { QuickPanelSheet(onClose = { finish() }) } }
    }

    override fun onStart() {
        super.onStart()
        Remote.hold("panel")
    }

    override fun onStop() {
        super.onStop()
        Remote.release("panel")
        finish()   // it's a quick panel: gone once something else covers it
    }

    override fun finish() {
        super.finish()
        noWindowAnimation(opening = false)
    }

    /** The system's window animation (slow on HyperOS) is skipped; the sheet animates itself. */
    private fun noWindowAnimation(opening: Boolean) {
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            overrideActivityTransition(if (opening) OVERRIDE_TRANSITION_OPEN else OVERRIDE_TRANSITION_CLOSE, 0, 0)
        } else {
            @Suppress("DEPRECATION")
            overridePendingTransition(0, 0)
        }
    }
}

/**
 * The same panel shown by the Quick Settings tile as a dialog over the shade (TileService.showDialog): no activity is
 * started, so Android's limits on starting screens from the background (the 2–3 s waits, the taps that did nothing)
 * do not apply. The process is already running while the shade is open, so the sheet is drawn at once.
 */
class QuickPanelDialog(context: android.content.Context) :
    androidx.activity.ComponentDialog(context, com.greypaulino.switchmonitor.R.style.Theme_SwitchMonitor_Panel) {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window?.let { w ->
            w.setLayout(android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.MATCH_PARENT)
            w.setBackgroundDrawable(android.graphics.drawable.ColorDrawable(android.graphics.Color.TRANSPARENT))
            w.clearFlags(android.view.WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            w.setWindowAnimations(0)
            androidx.core.view.WindowCompat.setDecorFitsSystemWindows(w, false)
        }
        setContentView(androidx.compose.ui.platform.ComposeView(context).apply {
            setContent { SwitchMonitorTheme { QuickPanelSheet(onClose = { dismiss() }) } }
        })
    }

    override fun onStart() {
        super.onStart()
        Remote.hold("panel")
    }

    override fun onStop() {
        super.onStop()
        Remote.release("panel")
    }
}

/**
 * The quick panel's sheet: rises in 0.15 s, and on close (outside, ✕ or Back) slides down and fades in 0.12 s before
 * [onClose] removes the window, instead of vanishing at once.
 */
@Composable
internal fun QuickPanelSheet(onClose: () -> Unit) {
    val ui by Remote.ui.collectAsState()
    val state = ui.state ?: Remote.latest
    // How long from the tap on the tile to the first frame (logcat, tag "switchmonitor").
    LaunchedEffect(Unit) {
        QuickTile.tappedAt.takeIf { it > 0 }?.let {
            android.util.Log.d("switchmonitor", "Quick panel shown ${android.os.SystemClock.uptimeMillis() - it} ms after the tap")
            QuickTile.tappedAt = 0
        }
    }
    val rise = remember { androidx.compose.animation.core.Animatable(1f) }
    LaunchedEffect(Unit) { rise.animateTo(0f, androidx.compose.animation.core.tween(150, easing = androidx.compose.animation.core.LinearOutSlowInEasing)) }
    val scope = rememberCoroutineScope()
    var closing by remember { mutableStateOf(false) }
    fun close() {
        if (closing) return
        closing = true
        scope.launch {
            rise.animateTo(1f, androidx.compose.animation.core.tween(120, easing = androidx.compose.animation.core.FastOutLinearInEasing))
            onClose()
        }
    }
    androidx.activity.compose.BackHandler { close() }
    Box(
        Modifier
            .fillMaxSize()
            .graphicsLayer { alpha = 1f - rise.value }
            .background(androidx.compose.ui.graphics.Color(0x99000000))
            .clickable(remember { MutableInteractionSource() }, indication = null) { close() },
        contentAlignment = Alignment.BottomCenter,
    ) {
        Column(
            Modifier
                .graphicsLayer { translationY = rise.value * 120.dp.toPx(); alpha = 1f - rise.value }
                .safeDrawingPadding()
                .padding(12.dp)
                .fillMaxWidth()
                .clip(RoundedCornerShape(28.dp))
                .background(Card)
                .border(1.dp, Line, RoundedCornerShape(28.dp))
                .clickable(remember { MutableInteractionSource() }, indication = null) { }
                .padding(start = 16.dp, end = 16.dp, top = 12.dp, bottom = 18.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Brightness", Modifier.weight(1f), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Text)
                if (state != null && state.canLink) com.greypaulino.switchmonitor.ui.LinkButton(state.linked)
                IconButton(onClick = { close() }) {
                    Box(Modifier.size(36.dp).clip(CircleShape).background(Raised), contentAlignment = Alignment.Center) {
                        Icon(Glyphs.Close, "Close", Modifier.size(16.dp), tint = Text)
                    }
                }
            }
            if (!Remote.isPaired) {
                Text("Open SwitchMonitor and pair this phone first.", Modifier.padding(vertical = 12.dp), color = Muted, fontSize = 14.sp)
            } else if (state == null) {
                // Paired, nothing received yet: placeholders the size of a display, the volume and the profiles.
                Column(Modifier.padding(top = 10.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Bone(Modifier.size(8.dp), CircleShape)
                        Bone(Modifier.padding(start = 10.dp).size(18.dp))
                        Bone(Modifier.padding(start = 10.dp).width(160.dp).height(14.dp))
                        androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
                        Bone(Modifier.width(36.dp).height(14.dp))
                    }
                    SliderBone(Modifier.padding(top = 2.dp))
                }
                Box(Modifier.padding(top = 10.dp).fillMaxWidth().height(1.dp).background(Line))
                Row(Modifier.padding(top = 8.dp).height(40.dp), verticalAlignment = Alignment.CenterVertically) {
                    Bone(Modifier.size(34.dp), CircleShape)
                    SliderBone(Modifier.weight(1f).padding(start = 12.dp, end = 12.dp))
                    Bone(Modifier.width(36.dp).height(14.dp))
                }
                Box(Modifier.padding(top = 8.dp).fillMaxWidth().height(1.dp).background(Line))
                Row(Modifier.padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    repeat(5) {
                        Column(Modifier.width(56.dp).padding(vertical = 2.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                            Bone(Modifier.size(46.dp).padding(4.dp), CircleShape)
                            Bone(Modifier.padding(top = 7.dp).width(36.dp).height(11.dp))
                        }
                    }
                }
            } else {
                val on = state.displays.filter { it.on }
                if (state.linked && on.size > 1) {
                    // Linked, as in the app: every display's name (green) and one bar they all follow.
                    val lead = on.firstOrNull { it.ready } ?: on.first()
                    val level = remember(lead.id) { mutableIntStateOf(lead.brightness ?: 0) }
                    Column(Modifier.padding(top = 6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        on.forEachIndexed { i, d -> DisplayLine(d, lit = true, value = if (i == 0) "${level.intValue}%" else "") }
                    }
                    PanelSlider(lead, level)
                } else {
                    for (d in on) {
                        val level = remember(d.id) { mutableIntStateOf(d.brightness ?: 0) }
                        Column(Modifier.padding(top = 10.dp)) {
                            DisplayLine(d, lit = d.selected, value = if (d.ready) "${level.intValue}%" else "…")
                            if (d.ready) PanelSlider(d, level) else SliderBone(Modifier.padding(top = 2.dp))
                        }
                    }
                }
                // The computer's Windows volume: mute button, slider and level.
                state.volume?.let { volume ->
                    Box(Modifier.padding(top = 10.dp).fillMaxWidth().height(1.dp).background(Line))
                    PanelVolume(volume)
                }
                if (state.profiles.isNotEmpty()) {
                    Box(Modifier.padding(top = 8.dp).fillMaxWidth().height(1.dp).background(Line))
                    Box(Modifier.padding(top = 12.dp)) {
                        FadingRow(spacing = 4.dp, selected = state.profiles.indexOfFirst { it.active }) {
                            items(state.profiles, key = { it.name }) { ProfileBadge(it) }
                        }
                    }
                }
                ConnectionHint(ui.phase)
            }
        }
    }
}

/**
 * Below the panel while the computer doesn't answer. After a few seconds without an answer it says why it usually
 * happens on Xiaomi / HyperOS (the system cuts the network of apps in the background unless their battery setting is
 * "No restrictions") and opens SwitchMonitor's app settings to change it.
 */
@Composable
private fun ConnectionHint(phase: com.greypaulino.switchmonitor.data.Phase) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var long by remember { mutableStateOf(false) }
    LaunchedEffect(phase) {
        long = false
        if (phase != com.greypaulino.switchmonitor.data.Phase.Connected) { delay(4000); long = true }
    }
    if (phase == com.greypaulino.switchmonitor.data.Phase.Connected) return
    if (!long) {
        Text("Connecting…", Modifier.padding(top = 6.dp), fontSize = 12.sp, color = Muted)
        return
    }
    Row(Modifier.padding(top = 10.dp).fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(
            "Can't reach the computer. If it only happens from here, set SwitchMonitor's battery use to No restrictions.",
            Modifier.weight(1f), fontSize = 12.sp, color = Muted, lineHeight = 16.sp,
        )
        Box(
            Modifier
                .padding(start = 10.dp)
                .clip(RoundedCornerShape(16.dp))
                .background(Raised)
                .border(1.dp, Line, RoundedCornerShape(16.dp))
                .clickable {
                    val intent = android.content.Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                        .setData(android.net.Uri.fromParts("package", context.packageName, null))
                        .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
                    try { context.startActivity(intent) } catch (_: Exception) { }
                }
                .padding(horizontal = 12.dp, vertical = 8.dp),
        ) { Text("Settings", fontSize = 13.sp, color = Text) }
    }
}

/** Windows volume in the panel: a round mute button, the slider (grey while muted) and the level. */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun PanelVolume(volume: com.greypaulino.switchmonitor.data.Volume) {
    val level = remember { mutableIntStateOf(volume.percent) }
    val scope = rememberCoroutineScope()
    var touching by remember { mutableStateOf(false) }
    LaunchedEffect(volume.percent, touching) { if (!touching) level.intValue = volume.percent }
    Row(Modifier.padding(top = 8.dp).height(40.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(
            Modifier
                .size(34.dp)
                .clip(CircleShape)
                .background(if (volume.muted) Text else Raised)
                .border(1.dp, if (volume.muted) Text else Line, CircleShape)
                .clickable(onClickLabel = if (volume.muted) "Turn the sound on" else "Mute") { Remote.mute(!volume.muted) },
            contentAlignment = Alignment.Center,
        ) {
            Icon(if (volume.muted) Glyphs.SpeakerOff else Glyphs.Speaker, if (volume.muted) "Muted" else "Sound on",
                Modifier.size(16.dp), tint = if (volume.muted) com.greypaulino.switchmonitor.ui.theme.Ink else Text)
        }
        androidx.compose.material3.Slider(
            value = level.intValue.toFloat(),
            onValueChange = {
                touching = true
                val v = it.roundToInt()
                if (v != level.intValue) { level.intValue = v; Remote.volume(v) }
            },
            onValueChangeFinished = { scope.launch { delay(600); touching = false } },
            valueRange = 0f..100f,
            thumb = { Box(Modifier.size(16.dp).shadow(2.dp, CircleShape).background(Text, CircleShape)) },
            track = { slider ->
                val fraction = (slider.value / 100f).coerceIn(0f, 1f)
                Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(Track)) {
                    Box(Modifier.fillMaxWidth(fraction).fillMaxHeight().background(if (volume.muted) Muted else Text))
                }
            },
            modifier = Modifier.weight(1f).padding(horizontal = 12.dp).height(36.dp),
        )
        Text(if (volume.muted) "Muted" else "${level.intValue}%", Modifier.width(48.dp), fontSize = 15.sp, color = com.greypaulino.switchmonitor.ui.theme.Soft,
            textAlign = androidx.compose.ui.text.style.TextAlign.End)
    }
}

/** The app's slider (thin white fill, small round thumb), 1 % steps; follows the computer again once the finger lifts. */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun PanelSlider(display: Display, level: androidx.compose.runtime.MutableIntState) {
    val scope = rememberCoroutineScope()
    var touching by remember { mutableStateOf(false) }
    LaunchedEffect(display.brightness, touching) { if (!touching) display.brightness?.let { level.intValue = it } }

    fun set(v: Int) {
        if (v == level.intValue) return
        level.intValue = v
        if (Remote.connected) Remote.brightness(display.id, v)
        else Remote.background(JSONObject().put("type", "brightness").put("display", display.id).put("value", v))
    }

    androidx.compose.material3.Slider(
        value = level.intValue.toFloat(),
        onValueChange = { touching = true; set(it.roundToInt()) },
        onValueChangeFinished = { scope.launch { delay(600); touching = false } },
        valueRange = 0f..100f,
        thumb = { Box(Modifier.size(16.dp).shadow(2.dp, CircleShape).background(Text, CircleShape)) },
        track = { slider ->
            val fraction = (slider.value / 100f).coerceIn(0f, 1f)
            Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(Track)) {
                Box(Modifier.fillMaxWidth(fraction).fillMaxHeight().background(Text))
            }
        },
        modifier = Modifier.padding(top = 2.dp).fillMaxWidth().height(36.dp),
    )
}
