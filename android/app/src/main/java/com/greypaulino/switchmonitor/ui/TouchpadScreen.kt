package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.data.RemoteUi
import com.greypaulino.switchmonitor.ui.theme.Card
import com.greypaulino.switchmonitor.ui.theme.Ground
import com.greypaulino.switchmonitor.ui.theme.Line
import com.greypaulino.switchmonitor.ui.theme.Muted
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.Soft
import com.greypaulino.switchmonitor.ui.theme.Text
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * The phone as the computer's mouse and keyboard: a touchpad (one finger moves, a tap clicks, two fingers scroll or, with a
 * tap, right-click), the three buttons, the system keyboard for text and a row of the keys a keyboard needs most.
 * The computer must allow it (SwitchMonitor > Settings > Devices > Phone as mouse and keyboard).
 */
@Composable
fun TouchpadScreen(ui: RemoteUi) {
    var keyboard by remember { mutableStateOf(false) }
    // While the touchpad is open the Wi-Fi radio stays awake (no power-save naps between messages) and the screen stays on.
    val context = androidx.compose.ui.platform.LocalContext.current
    androidx.compose.runtime.DisposableEffect(Unit) {
        val wifi = context.applicationContext.getSystemService(android.content.Context.WIFI_SERVICE) as? android.net.wifi.WifiManager
        @Suppress("DEPRECATION")
        val mode = if (android.os.Build.VERSION.SDK_INT >= 29) android.net.wifi.WifiManager.WIFI_MODE_FULL_LOW_LATENCY else android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF
        val lock = try { wifi?.createWifiLock(mode, "SwitchMonitor:touchpad")?.apply { setReferenceCounted(false); acquire() } } catch (_: Exception) { null }
        val window = (context as? android.app.Activity)?.window
        window?.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        onDispose {
            try { if (lock?.isHeld == true) lock.release() } catch (_: Exception) { }
            window?.clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }
    Column(
        Modifier.fillMaxSize().padding(horizontal = 16.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).padding(start = 4.dp)) {
                Text("Mouse and keyboard", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Text)
                Text(if (ui.group.size > 1) "Pick the computer to control" else "On this computer", fontSize = 13.sp, color = Muted)
            }
            Chip(if (keyboard) "Hide keyboard" else "Keyboard") { keyboard = !keyboard }
        }
        // Every computer of the group can be the one the mouse and keyboard act on.
        if (ui.group.size > 1) {
            val aimed = ui.target.ifEmpty { ui.gateway }
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (pc in ui.group) Chip(pc.name, selected = pc.deviceId == aimed) { Remote.aim(pc.deviceId) }
            }
        }

        Pad(Modifier.weight(1f).fillMaxWidth())

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            MouseButton("Left", "l", Modifier.weight(1f))
            MouseButton("Middle", "m", Modifier.weight(0.6f))
            MouseButton("Right", "r", Modifier.weight(1f))
        }

        KeysRow()
        if (keyboard) TypeBox()
    }
}

@Composable
private fun Chip(label: String, selected: Boolean = false, onClick: () -> Unit) {
    Box(
        Modifier.clip(RoundedCornerShape(14.dp)).background(if (selected) Text else Raised).border(1.dp, if (selected) Text else Line, RoundedCornerShape(14.dp))
            .clickable { onClick() }.padding(horizontal = 14.dp, vertical = 10.dp),
        contentAlignment = Alignment.Center,
    ) { Text(label, color = if (selected) Ground else Text, fontSize = 14.sp) }
}

/** The touchpad itself. */
@Composable
private fun Pad(modifier: Modifier) {
    val haptic = LocalHapticFeedback.current
    Box(
        modifier
            .clip(RoundedCornerShape(24.dp))
            .background(Card)
            .border(1.dp, Line, RoundedCornerShape(24.dp))
            .pointerInput(Unit) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    var moved = 0f
                    var fingers = 1
                    var carryX = 0f
                    var carryY = 0f
                    var scrollCarry = 0f
                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val down = event.changes.filter { it.pressed }
                        if (down.size > fingers) fingers = down.size
                        if (down.isEmpty()) break
                        if (down.size >= 2) {
                            // Two fingers: scroll (the average of both).
                            val dy = down.map { it.positionChange().y }.average().toFloat()
                            moved += abs(dy)
                            scrollCarry += dy
                            val notches = (scrollCarry / 14f).toInt()
                            if (notches != 0) {
                                scrollCarry -= notches * 14f
                                Remote.wheel(notches * 120)
                            }
                        } else {
                            val change = down[0]
                            val delta = change.positionChange()
                            moved += abs(delta.x) + abs(delta.y)
                            // A touchpad speeds up with the finger, smoothly: the gain follows the speed in px per millisecond.
                            val elapsed = (change.uptimeMillis - change.previousUptimeMillis).coerceAtLeast(1L)
                            val pace = kotlin.math.sqrt(delta.x * delta.x + delta.y * delta.y) / elapsed
                            val gain = (1.25f + 0.55f * pace).coerceAtMost(3.6f)
                            carryX += delta.x * gain
                            carryY += delta.y * gain
                            val dx = carryX.roundToInt()
                            val dy = carryY.roundToInt()
                            if (dx != 0 || dy != 0) {
                                carryX -= dx; carryY -= dy
                                Remote.pointer(dx, dy)
                            }
                        }
                        event.changes.forEach { it.consume() }
                    }
                    // A short touch that hardly moved is a click: one finger left, two fingers right.
                    if (moved < 24f) {
                        haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                        Remote.click(if (fingers >= 2) "r" else "l")
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Text("Move one finger to move the pointer\nTap to click · two fingers to scroll", color = Muted, fontSize = 13.sp, textAlign = TextAlign.Center)
    }
}

@Composable
private fun MouseButton(label: String, button: String, modifier: Modifier) {
    Box(
        modifier.height(52.dp).clip(RoundedCornerShape(16.dp)).background(Raised).border(1.dp, Line, RoundedCornerShape(16.dp))
            .pointerInput(button) {
                detectTapGestures(onPress = {
                    Remote.button(button, true)
                    tryAwaitRelease()
                    Remote.button(button, false)
                })
            },
        contentAlignment = Alignment.Center,
    ) { Text(label, color = Text, fontSize = 15.sp) }
}

/** Keys a keyboard needs that the phone's keyboard lacks (Windows virtual-key codes). */
@Composable
private fun KeysRow() {
    val keys = listOf(
        "Esc" to 0x1B, "Tab" to 0x09, "⌫" to 0x08, "Del" to 0x2E, "↵" to 0x0D,
        "←" to 0x25, "↑" to 0x26, "↓" to 0x28, "→" to 0x27, "Win" to 0x5B,
    )
    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        for ((label, code) in keys)
            Box(
                Modifier.width(54.dp).height(44.dp).clip(RoundedCornerShape(12.dp)).background(Raised).border(1.dp, Line, RoundedCornerShape(12.dp))
                    .pointerInput(code) {
                        detectTapGestures(onPress = {
                            Remote.key(code, false)
                            tryAwaitRelease()
                            Remote.key(code, true)
                        })
                    },
                contentAlignment = Alignment.Center,
            ) { Text(label, color = Text, fontSize = 14.sp) }
    }
}

/** What is typed here goes to the computer at once; the box stays empty. */
@Composable
private fun TypeBox() {
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    var value by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { focus.requestFocus(); keyboard?.show() }
    Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Raised).border(1.dp, Line, RoundedCornerShape(14.dp)).padding(14.dp)) {
        BasicTextField(
            value = value,
            onValueChange = { new ->
                if (new.isNotEmpty()) Remote.text(new)
                value = ""
            },
            modifier = Modifier.fillMaxWidth().focusRequester(focus).onPreviewKeyEvent { event ->
                if (event.key == Key.Backspace) {
                    if (event.type == KeyEventType.KeyDown) { Remote.key(0x08, false); Remote.key(0x08, true) }
                    true
                } else false
            },
            textStyle = androidx.compose.ui.text.TextStyle(color = Text, fontSize = 16.sp),
            cursorBrush = SolidColor(Text),
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(onSend = { Remote.key(0x0D, false); Remote.key(0x0D, true) }),
            decorationBox = { inner ->
                if (value.isEmpty()) Text("Type here: it goes to the computer", color = Soft, fontSize = 15.sp)
                inner()
            },
        )
    }
}
