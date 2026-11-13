package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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

@Composable
fun ConnectScreen(ui: RemoteUi) {
    // A phone that is not in the group yet joins it with the code of "My devices"; one that is (or prefers it) pairs with a phone code.
    var wantGroup by rememberSaveable { mutableStateOf(true) }
    val useGroup = wantGroup && !ui.inGroup
    Box(Modifier.fillMaxSize().background(Ground)) {
        Column(
            Modifier
                .fillMaxSize()
                .safeDrawingPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Column(Modifier.padding(horizontal = 4.dp, vertical = 8.dp)) {
                Box(Modifier.size(52.dp).clip(RoundedCornerShape(16.dp)).background(Raised), contentAlignment = Alignment.Center) {
                    Icon(Glyphs.Monitor, null, Modifier.size(26.dp), tint = Text)
                }
                Text(
                    when (ui.phase) {
                        Phase.NeedCode -> if (useGroup) "Add this phone" else "Pair this phone"
                        Phase.Connecting, Phase.Offline -> ui.computer ?: ui.host ?: "Connecting"
                        else -> "Connect to your computer"
                    },
                    Modifier.padding(top = 18.dp), fontSize = 26.sp, fontWeight = FontWeight.Bold, color = Text,
                )
                Text(
                    when (ui.phase) {
                        Phase.NeedCode ->
                            if (useGroup) "On the computer: SwitchMonitor › Settings › Devices › My devices. Type the 6-digit code it shows, then allow this phone there. Afterwards it connects to every computer of your group by itself."
                            else if (ui.inGroup) "This computer is not in your group. On it: SwitchMonitor › Settings › Devices › Phone › Pair a phone, and type the 6-digit code it shows (it lasts 2 minutes)."
                            else "On the computer: SwitchMonitor › Settings › Devices › Phone › Pair a phone. Type the 6-digit code it shows; it lasts 2 minutes."
                        Phase.Connecting -> "Connecting…"
                        Phase.Offline -> "Can't reach it right now. Check that the computer is on, SwitchMonitor is open with Phone turned on, and this phone is on the same Wi-Fi. Trying again…"
                        else -> "Your phone and the computer need to be on the same Wi-Fi, with Phone turned on in SwitchMonitor › Settings."
                    },
                    Modifier.padding(top = 8.dp), fontSize = 15.sp, lineHeight = 21.sp, color = Muted,
                )
            }

            val update by com.greypaulino.switchmonitor.data.Updates.state.collectAsState()
            TextButton(onClick = { com.greypaulino.switchmonitor.data.Updates.openDialog() }) {
                Text(
                    if (update.step == com.greypaulino.switchmonitor.data.Updates.Step.Available) "Update available: ${update.latest}" else "Check for updates (${update.current})",
                    color = if (update.step == com.greypaulino.switchmonitor.data.Updates.Step.Available) Green else Soft,
                )
            }
            when (ui.phase) {
                Phase.NeedCode -> CodePanel(ui, useGroup) { wantGroup = !wantGroup }
                Phase.Connecting, Phase.Offline -> {
                    if (ui.phase == Phase.Connecting) CircularProgressIndicator(Modifier.padding(start = 8.dp).size(28.dp), color = Text, strokeWidth = 3.dp)
                    TextButton(onClick = { Remote.forget() }) { Text("Use another computer", color = Soft) }
                }
                else -> FindPanel(ui)
            }
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

/**
 * The group's screen when none of its computers answers: nothing is chosen here, the app looks for them by itself and the control
 * screen appears as soon as one is found. "Use another computer" is only for one outside the group (by pairing code).
 */
@Composable
fun GroupOfflineScreen(ui: RemoteUi) {
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
                Text(
                    "No computer of your group is connected.",
                    Modifier.padding(top = 6.dp), fontSize = 16.sp, color = Soft, textAlign = TextAlign.Center,
                )
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
                Panel(padding = androidx.compose.foundation.layout.PaddingValues(14.dp)) {
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
                TextButton(onClick = { Remote.forget() }) { Text("Use another computer", color = Soft) }
            }
            val update by com.greypaulino.switchmonitor.data.Updates.state.collectAsState()
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
                TextButton(onClick = { com.greypaulino.switchmonitor.data.Updates.openDialog() }) {
                    Text(
                        if (update.step == com.greypaulino.switchmonitor.data.Updates.Step.Available) "Update available: ${update.latest}" else "Check for updates (${update.current})",
                        color = if (update.step == com.greypaulino.switchmonitor.data.Updates.Step.Available) Green else Soft,
                    )
                }
            }
        }
        ui.message?.let { Toast(it, Modifier.align(Alignment.BottomCenter)) }
    }
}

@Composable
private fun FindPanel(ui: RemoteUi) {
    var typing by rememberSaveable { mutableStateOf(false) }
    var address by rememberSaveable { mutableStateOf("") }
    // The group needs no choosing: the app connects to it by itself. Here only a computer outside it is picked, with its pairing code.
    if (ui.inGroup) {
        Panel(padding = androidx.compose.foundation.layout.PaddingValues(14.dp)) {
            Text("This is for a computer outside your group, paired with a code. Your group connects by itself.", Modifier.padding(4.dp), fontSize = 14.sp, color = Soft)
            Button(
                onClick = { Remote.backToGroup() },
                colors = ButtonDefaults.buttonColors(containerColor = Text, contentColor = Ink),
                modifier = Modifier.padding(top = 10.dp).fillMaxWidth().height(48.dp),
            ) { Text("Back to My devices", fontWeight = FontWeight.SemiBold) }
            TextButton(onClick = { Remote.leaveGroup() }) { Text("Leave the group on this phone", color = Soft) }
        }
    }
    // Paired computers (no code needed), with the address the search found now when it differs.
    if (ui.paired.isNotEmpty()) {
        Panel(padding = androidx.compose.foundation.layout.PaddingValues(14.dp)) {
            Text("Your computers", Modifier.padding(start = 4.dp, bottom = 10.dp), fontSize = 13.sp, color = Muted)
            for (pc in ui.paired) {
                val here = ui.found.firstOrNull { it.name.equals(pc.name, ignoreCase = true) }
                ComputerRow(pc.name, here?.host ?: pc.host, if (here != null) "Paired · on this Wi-Fi" else "Paired", here != null) {
                    Remote.choose(here?.host ?: pc.host, here?.port ?: pc.port, pc.name)
                }
            }
        }
    }
    val others = ui.found.filter { f -> ui.paired.none { it.name.equals(f.name, ignoreCase = true) } && ui.group.none { it.deviceId == f.deviceId } }
    // (a computer of the group is never offered here: it is reached through My devices)
    Panel(padding = androidx.compose.foundation.layout.PaddingValues(14.dp)) {
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

@Composable
private fun CodePanel(ui: RemoteUi, useGroup: Boolean, onSwitch: () -> Unit) {
    var code by rememberSaveable { mutableStateOf("") }
    fun submit() { if (code.length == 6 && !ui.enrolling) { if (useGroup) Remote.enroll(code) else Remote.pair(code) } }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    Panel {
        Text(if (useGroup) "Code of My devices" else "Pairing code", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Text)
        BasicTextField(
            value = code,
            onValueChange = { typed -> code = typed.filter { it.isDigit() }.take(6); if (code.length == 6) submit() },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { submit() }),
            textStyle = TextStyle(color = Ground),
            cursorBrush = androidx.compose.ui.graphics.SolidColor(Ground),
            modifier = Modifier.padding(top = 16.dp).fillMaxWidth().focusRequester(focus),
            decorationBox = { field ->
                Box(Modifier.size(1.dp).alpha(0f)) { field() }   // the real text field, kept invisible behind the six boxes
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    for (i in 0 until 6) {
                        val ch = code.getOrNull(i)?.toString() ?: ""
                        val current = i == code.length
                        Box(
                            Modifier
                                .weight(1f)
                                .height(56.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .background(Raised)
                                .border(1.dp, if (current) Text else Line, RoundedCornerShape(12.dp)),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(ch, fontSize = 24.sp, fontWeight = FontWeight.SemiBold, color = Text, textAlign = TextAlign.Center)
                        }
                    }
                }
            },
        )
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
        ) { Text(if (useGroup) "Add this phone" else "Pair", fontSize = 16.sp, fontWeight = FontWeight.SemiBold) }
        if (ui.enrolling) {
            Row(Modifier.padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(16.dp), color = Muted, strokeWidth = 2.dp)
                Text("Waiting for the computer to allow it…", Modifier.padding(start = 10.dp), fontSize = 13.sp, color = Muted)
            }
        }
        Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Glyphs.Check, null, Modifier.size(14.dp), tint = Green)
            Text(
                if (useGroup) "The code changes every 30 seconds, and the computer asks its owner to allow this phone."
                else "Only phones paired with a code can control the displays.",
                Modifier.padding(start = 6.dp), fontSize = 12.sp, color = Muted,
            )
        }
        if (!ui.inGroup) {
            TextButton(onClick = { code = ""; onSwitch() }) { Text(if (useGroup) "Use a phone pairing code instead" else "Use the code of My devices", color = Soft) }
        }
        TextButton(onClick = { Remote.forget() }) { Text("Use another computer", color = Soft) }
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
