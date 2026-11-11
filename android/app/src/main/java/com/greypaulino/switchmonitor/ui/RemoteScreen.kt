package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.data.AppItem
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.data.RemoteUi
import com.greypaulino.switchmonitor.ui.theme.Card
import com.greypaulino.switchmonitor.ui.theme.Green
import com.greypaulino.switchmonitor.ui.theme.Line
import com.greypaulino.switchmonitor.ui.theme.Muted
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.Soft
import com.greypaulino.switchmonitor.ui.theme.Text
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.sin

/**
 * The phone as a remote for any computer of the group: power at the top right, the control wheel (a media remote's) in the middle
 * of the screen with mute on its right, and the favorite programs of that computer below as labels with their icon.
 */
@Composable
fun RemoteScreen(ui: RemoteUi) {
    val target = ui.target.ifEmpty { ui.gateway }
    val name = ui.group.firstOrNull { it.deviceId == target }?.name ?: ui.computer ?: "this computer"
    var confirmOff by remember { mutableStateOf(false) }
    var countdown by remember { mutableIntStateOf(0) }
    LaunchedEffect(target) { Remote.requestApps() }
    LaunchedEffect(countdown) { if (countdown > 0) { delay(1000); countdown -= 1 } }

    Column(Modifier.fillMaxSize().padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).padding(start = 4.dp)) {
                Text("Remote", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Text)
                Text(if (countdown > 0) "Turning $name off in $countdown s" else if (ui.group.size > 1) "Pick the computer to control" else name, fontSize = 13.sp, color = Muted)
            }
            if (countdown > 0) { Chip("Cancel") { Remote.power("cancel"); countdown = 0 }; Box(Modifier.size(8.dp)) }
            // Power: top right.
            Box(
                Modifier.size(44.dp).clip(CircleShape).background(Raised).border(1.dp, Line, CircleShape).clickable(onClickLabel = "Turn off the computer") { confirmOff = true },
                contentAlignment = Alignment.Center,
            ) { Icon(Glyphs.Power, "Turn off", Modifier.size(22.dp), tint = Text) }
        }
        if (ui.group.size > 1) {
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (pc in ui.group) Chip(pc.name, selected = pc.deviceId == target) { Remote.aim(pc.deviceId) }
            }
        }

        // The wheel in the middle of what is left of the screen.
        Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            ControlWheel { Remote.media(it) }
        }

        Favorites(ui, target)
    }

    if (confirmOff) {
        AlertDialog(
            onDismissRequest = { confirmOff = false },
            containerColor = Raised,
            title = { Text("Turn off $name?", color = Text) },
            text = { Text("It goes off in 10 seconds; you can cancel until then.", color = Muted) },
            confirmButton = { TextButton(onClick = { confirmOff = false; Remote.power("shutdown"); countdown = 10 }) { Text("Turn off", color = Text) } },
            dismissButton = { TextButton(onClick = { confirmOff = false }) { Text("Cancel", color = Soft) } },
        )
    }
}

/** The wheel: the middle plays or pauses; up and down are volume; left and right skip back and forward. Volume repeats while held. */
@Composable
private fun ControlWheel(onKey: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    Box(
        Modifier
            .size(270.dp)
            .clip(CircleShape)
            .pointerInput(Unit) {
                detectTapGestures(onPress = { at ->
                    val centre = Offset(size.width / 2f, size.height / 2f)
                    val dx = at.x - centre.x
                    val dy = at.y - centre.y
                    val radius = size.width / 2f
                    val distance = hypot(dx, dy)
                    val key = when {
                        distance > radius -> null
                        distance < radius * 0.36f -> "playpause"
                        abs(dx) > abs(dy) -> if (dx > 0) "next" else "prev"
                        else -> if (dy < 0) "volup" else "voldown"
                    }
                    if (key != null) {
                        onKey(key)
                        if (key == "volup" || key == "voldown") {
                            val repeat = scope.launch { delay(400); while (true) { onKey(key); delay(120) } }
                            tryAwaitRelease()
                            repeat.cancel()
                        }
                    }
                })
            },
        contentAlignment = Alignment.Center,
    ) {
        Canvas(Modifier.size(270.dp)) {
            val centre = Offset(size.width / 2f, size.height / 2f)
            val r = size.minDimension / 2f
            val inner = r * 0.36f
            drawCircle(Raised, r, centre)
            drawCircle(Line, r - 0.5.dp.toPx(), centre, style = Stroke(1.dp.toPx()))
            for (degrees in listOf(45.0, 135.0, 225.0, 315.0)) {
                val a = Math.toRadians(degrees)
                drawLine(
                    Line, centre + Offset((cos(a) * inner).toFloat(), (sin(a) * inner).toFloat()),
                    centre + Offset((cos(a) * r).toFloat(), (sin(a) * r).toFloat()), 1.dp.toPx(),
                )
            }
            drawCircle(Card, inner, centre)
            drawCircle(Line, inner, centre, style = Stroke(1.dp.toPx()))
        }
        Text("+", Modifier.align(Alignment.TopCenter).padding(top = 26.dp), fontSize = 30.sp, color = Text)
        Text("−", Modifier.align(Alignment.BottomCenter).padding(bottom = 22.dp), fontSize = 34.sp, color = Text)
        Text("◀◀", Modifier.align(Alignment.CenterStart).padding(start = 22.dp), fontSize = 20.sp, color = Text)
        Text("▶▶", Modifier.align(Alignment.CenterEnd).padding(end = 22.dp), fontSize = 20.sp, color = Text)
        Text("▶ ❚❚", fontSize = 18.sp, color = Text, textAlign = TextAlign.Center)
    }
}

/** The favorite programs as labels: icon and name; a tap opens, a long press closes or removes. A green dot says it is open. */
@OptIn(ExperimentalLayoutApi::class, ExperimentalFoundationApi::class)
@Composable
private fun Favorites(ui: RemoteUi, target: String) {
    val favorites = ui.favorites[target].orEmpty()
    val offered = ui.apps[target]
    var picking by remember { mutableStateOf(false) }
    var options by remember { mutableStateOf<AppItem?>(null) }
    Panel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Favorite apps", Modifier.weight(1f), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Text)
            // Mute: at the top of this panel.
            Box(
                Modifier.size(40.dp).clip(CircleShape).background(Raised).border(1.dp, Line, CircleShape)
                    .clickable(onClickLabel = "Mute") { Remote.media("mute") },
                contentAlignment = Alignment.Center,
            ) { Icon(Glyphs.SpeakerOff, "Mute", Modifier.size(20.dp), tint = Text) }
            TextButton(onClick = { Remote.requestApps(); picking = true }) { Text("Add", color = Soft) }
        }
        if (offered != null && !offered.allowed) {
            Text("Turn on \"Phone as remote\" in SwitchMonitor > Settings > Devices on that computer to open and close programs.", fontSize = 13.sp, color = Muted)
        } else if (favorites.isEmpty()) {
            Text("No favorites yet. Add the programs you start most.", fontSize = 13.sp, color = Muted)
        } else {
            // A carousel: icons you drag sideways, the name under each.
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for (app in favorites) {
                    val running = offered?.items?.firstOrNull { it.id == app.id }?.running == true
                    Column(
                        Modifier.width(78.dp).clip(RoundedCornerShape(14.dp))
                            .combinedClickable(onClick = { Remote.launch(app.id) }, onLongClick = { options = app })
                            .padding(vertical = 6.dp, horizontal = 4.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Box(
                            Modifier.size(58.dp).clip(RoundedCornerShape(16.dp)).background(Raised).border(1.dp, Line, RoundedCornerShape(16.dp)),
                            contentAlignment = Alignment.Center,
                        ) {
                            val icon = ui.icons["$target|${app.id}"]
                            if (icon != null) Image(icon.asImageBitmap(), null, Modifier.size(36.dp))
                            else Icon(Glyphs.Monitor, null, Modifier.size(28.dp), tint = Muted)
                            if (running) Box(Modifier.align(Alignment.TopEnd).padding(5.dp).size(8.dp).clip(CircleShape).background(Green))
                        }
                        Text(app.name, Modifier.padding(top = 5.dp), fontSize = 12.sp, color = Text, maxLines = 2, textAlign = TextAlign.Center, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
            Text("Tap to open · hold to close or remove", Modifier.padding(top = 4.dp), fontSize = 12.sp, color = Muted)
        }
    }
    options?.let { app ->
        AlertDialog(
            onDismissRequest = { options = null },
            containerColor = Raised,
            title = { Text(app.name, color = Text) },
            text = { Text("Close it on the computer, or take it out of the favorites?", color = Muted) },
            confirmButton = { TextButton(onClick = { options = null; Remote.closeApp(app.id) }) { Text("Close it", color = Text) } },
            dismissButton = { TextButton(onClick = { options = null; Remote.removeFavorite(app.id) }) { Text("Remove", color = Soft) } },
        )
    }
    if (picking) AppPicker(offered?.items.orEmpty(), allowed = offered?.allowed != false, taken = favorites.map { it.id }.toSet()) { picking = false }
}

/** Every program of that computer's Start menu: tap one to make it a favorite. */
@Composable
private fun AppPicker(apps: List<AppItem>, allowed: Boolean, taken: Set<String>, onClose: () -> Unit) {
    var query by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onClose,
        containerColor = Raised,
        title = { Text("Add a favorite", color = Text) },
        text = {
            Column {
                OutlinedTextField(
                    value = query, onValueChange = { query = it }, singleLine = true, placeholder = { Text("Search programs") },
                    colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = Text, unfocusedBorderColor = Line, cursorColor = Text),
                    modifier = Modifier.fillMaxWidth(),
                )
                val shown = apps.filter { it.name.contains(query, ignoreCase = true) }
                if (!allowed) Text("That computer has not allowed it: turn on \"Phone as remote\" there.", Modifier.padding(top = 12.dp), color = Muted, fontSize = 13.sp)
                else if (apps.isEmpty()) Text("Asking the computer for its programs…", Modifier.padding(top = 12.dp), color = Muted, fontSize = 13.sp)
                LazyColumn(Modifier.height(320.dp).padding(top = 8.dp)) {
                    items(shown, key = { it.id }) { app ->
                        val added = app.id in taken
                        Row(
                            Modifier.fillMaxWidth().clickable(enabled = !added) { Remote.addFavorite(app) }.padding(vertical = 12.dp, horizontal = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(app.name, Modifier.weight(1f), color = if (added) Muted else Text, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            if (added) Text("Added", color = Muted, fontSize = 12.sp)
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onClose) { Text("Done", color = Text) } },
    )
}
