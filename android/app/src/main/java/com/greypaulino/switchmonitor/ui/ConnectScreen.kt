package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.data.Phase
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.data.RemoteUi
import com.greypaulino.switchmonitor.ui.theme.Green
import com.greypaulino.switchmonitor.ui.theme.Ground
import com.greypaulino.switchmonitor.ui.theme.Ink
import com.greypaulino.switchmonitor.ui.theme.Line
import com.greypaulino.switchmonitor.ui.theme.Muted
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.Soft
import com.greypaulino.switchmonitor.ui.theme.Text

private val Pad = androidx.compose.foundation.layout.PaddingValues(14.dp)

// ---- The start: the two ways to connect ----

/** What the phone asks first: join the group of the person's computers (My devices), or connect to one computer. */
@Composable
fun StartScreen(ui: RemoteUi) {
    Box(Modifier.fillMaxSize().background(Ground)) {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Column(Modifier.padding(horizontal = 4.dp, vertical = 8.dp)) {
                Box(Modifier.size(52.dp).clip(RoundedCornerShape(16.dp)).background(Raised), contentAlignment = Alignment.Center) {
                    Icon(Glyphs.Monitor, null, Modifier.size(26.dp), tint = Text)
                }
                Text("SwitchMonitor", Modifier.padding(top = 18.dp), fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Text)
                Text("How do you want to connect?", Modifier.padding(top = 8.dp), fontSize = 15.sp, lineHeight = 21.sp, color = Muted)
            }
            ModeCard(
                Glyphs.Link, "Join a group",
                if (ui.inGroup) "This phone is already in your group of devices (My devices). Open it." else "Add this phone to My devices, the group of your computers, with the code one of them shows. After that it connects to all of them by itself.",
            ) { Remote.chooseMode("group") }
            ModeCard(
                Glyphs.Monitor, "Connect to one computer",
                "Pair with a single computer using the code of its Settings › Devices › Phone, without a group.",
            ) { Remote.chooseMode("computer") }
            UpdateButton()
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

@Composable
private fun ModeCard(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, text: String, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(20.dp)).background(Raised).border(1.dp, Line, RoundedCornerShape(20.dp)).clickable(onClick = onClick).padding(18.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(46.dp).clip(RoundedCornerShape(14.dp)).background(Ground), contentAlignment = Alignment.Center) {
            Icon(icon, null, Modifier.size(24.dp), tint = Text)
        }
        Column(Modifier.weight(1f).padding(horizontal = 14.dp)) {
            Text(title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Text)
            Text(text, Modifier.padding(top = 4.dp), fontSize = 13.sp, lineHeight = 18.sp, color = Muted)
        }
        Icon(Glyphs.ChevronRight, null, Modifier.size(18.dp), tint = Muted)
    }
}

@Composable
private fun UpdateButton() {
    val update by com.greypaulino.switchmonitor.data.Updates.state.collectAsState()
    val available = update.step == com.greypaulino.switchmonitor.data.Updates.Step.Available
    TextButton(onClick = { com.greypaulino.switchmonitor.data.Updates.openDialog() }) {
        Text(if (available) "Update available: ${update.latest}" else "Check for updates (${update.current})", color = if (available) Green else Soft)
    }
}

// ---- Joining the group: only the code, whatever the connection does meanwhile ----

/**
 * The phone chose the group and is not in it: it asks for the code of a computer of the group and nothing else. The computer is
 * connected to behind the scenes (the only one found, or the one picked); after the code and the approval on that computer the app
 * goes to the discovery screen until it is connected to the whole group.
 */
@Composable
fun JoinGroupScreen(ui: RemoteUi) {
    var code by rememberSaveable { mutableStateOf("") }
    // One computer on the Wi-Fi: no need to pick it.
    LaunchedEffect(ui.found, ui.host) {
        if (ui.host == null && ui.found.size == 1) ui.found.first().let { Remote.choose(it.host, it.port, it.name, viaGroup = true) }
    }
    val picked = ui.found.firstOrNull { it.host == ui.host }
    Box(Modifier.fillMaxSize().background(Ground)) {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            TextButton(onClick = { Remote.forget() }) { Text("‹ Back", color = Soft) }
            Column(Modifier.padding(horizontal = 4.dp)) {
                Text("Join a group", fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Text)
                Text(
                    "On a computer of your group open SwitchMonitor › Settings › Devices › My devices. Type the 6-digit code it shows, then allow this phone there.",
                    Modifier.padding(top = 8.dp), fontSize = 15.sp, lineHeight = 21.sp, color = Muted,
                )
            }
            Panel(padding = Pad) {
                Row(Modifier.padding(start = 4.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Glyphs.Wifi, null, Modifier.size(16.dp), tint = Muted)
                    Text("Computer that shows the code", Modifier.weight(1f).padding(start = 8.dp), fontSize = 13.sp, color = Muted)
                    if (ui.searching) CircularProgressIndicator(Modifier.size(16.dp), color = Muted, strokeWidth = 2.dp)
                    else Icon(Glyphs.Refresh, "Search again", Modifier.size(32.dp).clip(RoundedCornerShape(16.dp)).clickable { Remote.search() }.padding(7.dp), tint = Soft)
                }
                if (ui.found.isEmpty()) {
                    Text(if (ui.searching) "Searching…" else "No computer answered. Is SwitchMonitor open with Phone turned on, on this Wi-Fi?", Modifier.padding(4.dp), fontSize = 14.sp, color = Soft)
                } else {
                    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (pc in ui.found) Chip(pc.name, selected = pc.host == ui.host, icon = Glyphs.Monitor) {
                            code = ""
                            Remote.choose(pc.host, pc.port, pc.name, viaGroup = true)
                        }
                    }
                }
            }
            Panel {
                Text("Code of My devices", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Text)
                val ready = ui.ready && picked != null || ui.ready && ui.host != null
                CodeEntry(code, enabled = ready && !ui.enrolling, onCode = { typed ->
                    code = typed
                    if (typed.length == 6 && ready && !ui.enrolling) Remote.enroll(typed)
                })
                if (!ready) {
                    Row(Modifier.padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        if (ui.host != null) CircularProgressIndicator(Modifier.size(16.dp), color = Muted, strokeWidth = 2.dp)
                        Text(
                            if (ui.host != null) "Connecting to ${picked?.name ?: ui.computer ?: ui.host}…" else "Pick the computer that shows the code.",
                            Modifier.padding(start = if (ui.host != null) 10.dp else 0.dp), fontSize = 13.sp, color = Muted,
                        )
                    }
                }
                if (ui.verification.isNotEmpty() && ready) {
                    // The computer shows this same number when it asks to allow this phone: if it differs, somebody is in between.
                    Text("Verification number", Modifier.padding(top = 16.dp), fontSize = 12.sp, color = Muted)
                    Text(ui.verification, fontSize = 28.sp, fontWeight = FontWeight.SemiBold, color = Text)
                    Text("The computer shows the same number when it asks to allow this phone. If it differs, do not allow it.", fontSize = 12.sp, color = Muted)
                }
                Spacer(Modifier.height(16.dp))
                Button(
                    onClick = { if (code.length == 6) Remote.enroll(code) }, enabled = code.length == 6 && ready && !ui.enrolling,
                    colors = ButtonDefaults.buttonColors(containerColor = Text, contentColor = Ink),
                    modifier = Modifier.fillMaxWidth().height(52.dp),
                ) { Text("Add this phone", fontSize = 16.sp, fontWeight = FontWeight.SemiBold) }
                if (ui.enrolling) {
                    Row(Modifier.padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(16.dp), color = Muted, strokeWidth = 2.dp)
                        Text("Waiting for the computer to allow it…", Modifier.padding(start = 10.dp), fontSize = 13.sp, color = Muted)
                    }
                }
                Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Glyphs.Check, null, Modifier.size(14.dp), tint = Green)
                    Text("The code changes every 30 seconds, and the computer asks its owner to allow this phone.", Modifier.padding(start = 6.dp), fontSize = 12.sp, color = Muted)
                }
            }
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

// ---- One computer, paired by code ----

@Composable
fun ConnectScreen(ui: RemoteUi) {
    Box(Modifier.fillMaxSize().background(Ground)) {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            TextButton(onClick = { Remote.forget() }) { Text("‹ Back", color = Soft) }
            Column(Modifier.padding(horizontal = 4.dp, vertical = 4.dp)) {
                Text(
                    when (ui.phase) {
                        Phase.NeedCode -> "Pair this phone"
                        Phase.Connecting, Phase.Offline -> ui.computer ?: ui.host ?: "Connecting"
                        else -> "Connect to one computer"
                    },
                    fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Text,
                )
                Text(
                    when (ui.phase) {
                        Phase.NeedCode -> "On the computer: SwitchMonitor › Settings › Devices › Phone › Pair a phone. Type the 6-digit code it shows; it lasts 2 minutes."
                        Phase.Connecting -> "Connecting…"
                        Phase.Offline -> "Can't reach it right now. Check that the computer is on, SwitchMonitor is open with Phone turned on, and this phone is on the same Wi-Fi. Trying again…"
                        else -> "Your phone and the computer need to be on the same Wi-Fi, with Phone turned on in SwitchMonitor › Settings."
                    },
                    Modifier.padding(top = 8.dp), fontSize = 15.sp, lineHeight = 21.sp, color = Muted,
                )
            }
            when (ui.phase) {
                Phase.NeedCode -> CodePanel(ui)
                Phase.Connecting, Phase.Offline -> {
                    if (ui.phase == Phase.Connecting) CircularProgressIndicator(Modifier.padding(start = 8.dp).size(28.dp), color = Text, strokeWidth = 3.dp)
                    TextButton(onClick = { Remote.chooseMode("computer") }) { Text("Choose another computer", color = Soft) }
                }
                else -> FindPanel(ui)
            }
            UpdateButton()
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

/**
 * The group's screen while none of its computers answers (or while the phone has just joined and is looking for them): nothing is
 * chosen here, the app looks by itself and the control screen appears as soon as one is found.
 */
@Composable
fun GroupOfflineScreen(ui: RemoteUi) {
    var confirmLeave by remember { mutableStateOf(false) }
    if (confirmLeave) LeaveGroupDialog(onDismiss = { confirmLeave = false })
    Box(Modifier.fillMaxSize().background(Ground)) {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Column(Modifier.fillMaxWidth().padding(top = 36.dp, bottom = 8.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Box(Modifier.size(84.dp).clip(RoundedCornerShape(26.dp)).background(Raised).border(1.dp, Line, RoundedCornerShape(26.dp)), contentAlignment = Alignment.Center) {
                    Icon(Glyphs.Wifi, null, Modifier.size(38.dp), tint = Muted)
                }
                Text("My devices", Modifier.padding(top = 22.dp), fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Text)
                Text("No computer of your group is connected.", Modifier.padding(top = 6.dp), fontSize = 16.sp, color = Soft, textAlign = TextAlign.Center)
                Row(Modifier.padding(top = 14.dp), verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(15.dp), color = Muted, strokeWidth = 2.dp)
                    Text("Looking for them on this Wi-Fi…", Modifier.padding(start = 10.dp), fontSize = 14.sp, color = Muted)
                }
                Text(
                    "Turn on a computer with SwitchMonitor open and Phone enabled. When one appears, the controls open by themselves.",
                    Modifier.padding(top = 14.dp, start = 12.dp, end = 12.dp), fontSize = 14.sp, lineHeight = 20.sp, color = Muted, textAlign = TextAlign.Center,
                )
            }
            if (ui.group.isNotEmpty()) {
                Panel(padding = Pad) {
                    Text("Computers in your group", Modifier.padding(start = 4.dp, bottom = 10.dp), fontSize = 13.sp, color = Muted)
                    for (pc in ui.group) {
                        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp, horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.size(8.dp).clip(androidx.compose.foundation.shape.CircleShape).background(Muted))
                            Text(pc.name, Modifier.weight(1f).padding(start = 12.dp), fontSize = 16.sp, color = Text)
                            Text("Not connected", fontSize = 13.sp, color = Muted)
                        }
                    }
                }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
                TextButton(onClick = { Remote.search() }, enabled = !ui.searching) { Text("Search again", color = Soft) }
                TextButton(onClick = { Remote.forget() }) { Text("Change how I connect", color = Soft) }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
                TextButton(onClick = { confirmLeave = true }) { Text("Leave the group on this phone", color = Soft) }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) { UpdateButton() }
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

/** Leaving the group: the other devices are told, and each one takes this phone out of its list. */
@Composable
fun LeaveGroupDialog(onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = Raised,
        title = { Text("Leave the group?", color = Text) },
        text = { Text("Your computers will take this phone out of their lists. To come back you type the code of one of them again.", color = Soft) },
        confirmButton = { TextButton(onClick = { onDismiss(); Remote.leaveGroup() }) { Text("Leave", color = Text) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel", color = Soft) } },
    )
}

@Composable
private fun FindPanel(ui: RemoteUi) {
    var typing by rememberSaveable { mutableStateOf(false) }
    var address by rememberSaveable { mutableStateOf("") }
    // Paired computers (no code needed), with the address the search found now when it differs.
    if (ui.paired.isNotEmpty()) {
        Panel(padding = Pad) {
            Text("Your computers", Modifier.padding(start = 4.dp, bottom = 10.dp), fontSize = 13.sp, color = Muted)
            for (pc in ui.paired) {
                val here = ui.found.firstOrNull { it.name.equals(pc.name, ignoreCase = true) }
                ComputerRow(pc.name, here?.host ?: pc.host, if (here != null) "Paired · on this Wi-Fi" else "Paired", here != null) {
                    Remote.choose(here?.host ?: pc.host, here?.port ?: pc.port, pc.name)
                }
            }
        }
    }
    val others = ui.found.filter { f -> ui.paired.none { it.name.equals(f.name, ignoreCase = true) } }
    Panel(padding = Pad) {
        Row(Modifier.padding(start = 4.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Glyphs.Wifi, null, Modifier.size(16.dp), tint = Muted)
            Text("Found on this Wi-Fi", Modifier.weight(1f).padding(start = 8.dp), fontSize = 13.sp, color = Muted)
            if (ui.searching) CircularProgressIndicator(Modifier.size(16.dp), color = Muted, strokeWidth = 2.dp)
            else Icon(Glyphs.Refresh, "Search again", Modifier.size(32.dp).clip(RoundedCornerShape(16.dp)).clickable { Remote.search() }.padding(7.dp), tint = Soft)
        }
        if (others.isEmpty()) {
            Text(
                when {
                    ui.searching -> "Searching…"
                    ui.found.isNotEmpty() -> "No other computer found."
                    else -> "No computer answered. Is SwitchMonitor open with Phone turned on?"
                },
                Modifier.padding(horizontal = 4.dp, vertical = 6.dp), fontSize = 14.sp, color = Soft,
            )
        }
        for (pc in others) ComputerRow(pc.name, pc.host, null, false) { Remote.choose(pc.host, pc.port, pc.name) }
        if (!typing) {
            TextButton(onClick = { typing = true }) { Text("Not listed? Type its address", color = Soft) }
        } else {
            Text("The address is in SwitchMonitor › Settings › Phone.", Modifier.padding(4.dp), fontSize = 13.sp, color = Muted)
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = address, onValueChange = { address = it.trim() },
                    placeholder = { Text("10.0.0.45") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Go),
                    keyboardActions = KeyboardActions(onGo = { if (address.isNotBlank()) Remote.choose(address) }),
                    colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = Text, unfocusedBorderColor = Line, cursorColor = Text),
                    modifier = Modifier.weight(1f),
                )
                Button(
                    onClick = { Remote.choose(address) }, enabled = address.isNotBlank(),
                    colors = ButtonDefaults.buttonColors(containerColor = Text, contentColor = Ink),
                    modifier = Modifier.padding(start = 8.dp).height(52.dp),
                ) { Text("Connect") }
            }
        }
    }
}

/** The six boxes of a code; the real text field is kept invisible behind them. */
@Composable
private fun CodeEntry(code: String, enabled: Boolean, onCode: (String) -> Unit) {
    val focus = remember { FocusRequester() }
    LaunchedEffect(enabled) { if (enabled) try { focus.requestFocus() } catch (_: Exception) { } }
    BasicTextField(
        value = code,
        onValueChange = { typed -> if (enabled) onCode(typed.filter { it.isDigit() }.take(6)) },
        enabled = enabled,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
        keyboardActions = KeyboardActions(onDone = { if (code.length == 6) onCode(code) }),
        textStyle = TextStyle(color = Ground),
        cursorBrush = androidx.compose.ui.graphics.SolidColor(Ground),
        modifier = Modifier.padding(top = 16.dp).fillMaxWidth().focusRequester(focus),
        decorationBox = { field ->
            Box(Modifier.size(1.dp).alpha(0f)) { field() }
            Row(Modifier.alpha(if (enabled) 1f else 0.45f), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (i in 0 until 6) {
                    val ch = code.getOrNull(i)?.toString() ?: ""
                    val current = enabled && i == code.length
                    Box(
                        Modifier.weight(1f).height(56.dp).clip(RoundedCornerShape(12.dp)).background(Raised)
                            .border(1.dp, if (current) Text else Line, RoundedCornerShape(12.dp)),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(ch, fontSize = 24.sp, fontWeight = FontWeight.SemiBold, color = Text, textAlign = TextAlign.Center)
                    }
                }
            }
        },
    )
}

/** The pairing code of one computer (Settings › Devices › Phone › Pair a phone): afterwards the controls open. */
@Composable
private fun CodePanel(ui: RemoteUi) {
    var code by rememberSaveable { mutableStateOf("") }
    fun submit() { if (code.length == 6 && !ui.enrolling) Remote.pair(code) }
    Panel {
        Text("Pairing code", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Text)
        CodeEntry(code, enabled = !ui.enrolling, onCode = { typed -> code = typed; if (typed.length == 6) submit() })
        if (ui.verification.isNotEmpty()) {
            // The computer shows this same number when it asks its owner to allow this phone: if it differs, somebody is in between.
            Text("Verification number", Modifier.padding(top = 16.dp), fontSize = 12.sp, color = Muted)
            Text(ui.verification, fontSize = 28.sp, fontWeight = FontWeight.SemiBold, color = Text)
            Text("The computer shows the same number when it asks to allow this phone. If it differs, do not allow it.", fontSize = 12.sp, color = Muted)
        }
        Spacer(Modifier.height(16.dp))
        Button(
            onClick = { submit() }, enabled = code.length == 6 && !ui.enrolling,
            colors = ButtonDefaults.buttonColors(containerColor = Text, contentColor = Ink),
            modifier = Modifier.fillMaxWidth().height(52.dp),
        ) { Text("Pair", fontSize = 16.sp, fontWeight = FontWeight.SemiBold) }
        if (ui.enrolling) {
            Row(Modifier.padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(16.dp), color = Muted, strokeWidth = 2.dp)
                Text("Waiting for the computer to allow it…", Modifier.padding(start = 10.dp), fontSize = 13.sp, color = Muted)
            }
        }
        Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Glyphs.Check, null, Modifier.size(14.dp), tint = Green)
            Text("Only phones paired with a code can control the displays.", Modifier.padding(start = 6.dp), fontSize = 12.sp, color = Muted)
        }
        TextButton(onClick = { Remote.chooseMode("computer") }) { Text("Choose another computer", color = Soft) }
    }
}

@Composable
private fun ComputerRow(name: String, host: String, note: String?, online: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(bottom = 8.dp)
            .clip(RoundedCornerShape(16.dp))
            .background(Raised)
            .border(1.dp, Line, RoundedCornerShape(16.dp))
            .clickable(onClick = onClick)
            .padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(name, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Text)
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (note != null) {
                    Box(Modifier.size(7.dp).clip(androidx.compose.foundation.shape.CircleShape).background(if (online) Green else Muted))
                    Text(note, Modifier.padding(start = 6.dp, end = 8.dp), fontSize = 13.sp, color = Soft)
                }
                Text(host, fontSize = 13.sp, color = Muted)
            }
        }
        Icon(Glyphs.ChevronRight, null, Modifier.size(18.dp), tint = Muted)
    }
}
