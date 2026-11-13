package com.greypaulino.switchmonitor.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.greypaulino.switchmonitor.data.Updates
import com.greypaulino.switchmonitor.ui.theme.Green
import com.greypaulino.switchmonitor.ui.theme.Ink
import com.greypaulino.switchmonitor.ui.theme.Muted
import com.greypaulino.switchmonitor.ui.theme.Raised
import com.greypaulino.switchmonitor.ui.theme.Soft
import com.greypaulino.switchmonitor.ui.theme.Text
import com.greypaulino.switchmonitor.ui.theme.Track
import com.greypaulino.switchmonitor.ui.theme.Warn
import java.text.DateFormat
import java.util.Date

/** "Updates": the installed version, a button to look for a new one, and (when there is one) to download and install it. */
@Composable
fun UpdateDialog() {
    val open by Updates.dialog.collectAsState()
    if (!open) return
    val state by Updates.state.collectAsState()
    val context = LocalContext.current
    val busy = state.step == Updates.Step.Checking || state.step == Updates.Step.Downloading

    AlertDialog(
        onDismissRequest = { Updates.closeDialog() },
        containerColor = Raised,
        title = { Text("Updates", color = Text) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Installed: ${state.current}", color = Soft, fontSize = 14.sp)
                when (state.step) {
                    Updates.Step.Checking -> Text("Looking for updates…", color = Muted, fontSize = 14.sp)
                    Updates.Step.UpToDate -> Text("SwitchMonitor is up to date.", color = Green, fontSize = 14.sp)
                    Updates.Step.Available, Updates.Step.Downloading, Updates.Step.Ready -> {
                        Text("Version ${state.latest} is available.", color = Text, fontSize = 16.sp)
                        if (state.notes.isNotEmpty() && state.step == Updates.Step.Available) Text(state.notes, color = Muted, fontSize = 13.sp, lineHeight = 18.sp)
                        if (state.step == Updates.Step.Downloading) {
                            LinearProgressIndicator(progress = { state.progress }, Modifier.fillMaxWidth(), color = Green, trackColor = Track)
                            Text("Downloading… ${(state.progress * 100).toInt()}%", color = Muted, fontSize = 13.sp)
                        }
                        if (state.step == Updates.Step.Ready) Text(
                            if (state.needsPermission) "Allow SwitchMonitor to install apps in the screen Android just opened, then come back and press Install."
                            else "Downloaded and checked. Android asks you to confirm the installation.",
                            color = Muted, fontSize = 13.sp,
                        )
                    }
                    Updates.Step.Failed -> Text(state.error ?: "Something went wrong.", color = Warn, fontSize = 14.sp)
                    Updates.Step.Idle -> if (state.lastCheck == 0L) Text("Not checked yet.", color = Muted, fontSize = 14.sp)
                }
                if (state.lastCheck > 0) Text(
                    "Last checked ${DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(state.lastCheck))}",
                    color = Muted, fontSize = 12.sp,
                )
                Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Check automatically", Modifier.weight(1f), color = Text, fontSize = 14.sp)
                    Switch(
                        checked = state.autoCheck, onCheckedChange = { Updates.setAutoCheck(context, it) },
                        colors = SwitchDefaults.colors(checkedTrackColor = Green, checkedThumbColor = Ink),
                    )
                }
                Text("Once a day, and when the app opens. A notification tells you when there is a new version.", color = Muted, fontSize = 12.sp)
            }
        },
        confirmButton = {
            val newOne = state.step == Updates.Step.Available || (state.step == Updates.Step.Failed && state.latest != null)
            if (state.step == Updates.Step.Ready) TextButton(onClick = { Updates.installDownloaded(context) }) { Text("Install", color = Green) }
            else if (newOne || state.step == Updates.Step.Downloading)
                TextButton(onClick = { Updates.downloadAndInstall(context) }, enabled = !busy) { Text("Download and install", color = Green) }
            else TextButton(onClick = { Updates.check(context, manual = true) }, enabled = !busy) { Text("Check now", color = Green) }
        },
        dismissButton = {
            Row {
                if (state.step == Updates.Step.Available || state.step == Updates.Step.Ready || (state.step == Updates.Step.Failed && state.latest != null))
                    TextButton(onClick = { Updates.check(context, manual = true) }, enabled = !busy) { Text("Check again", color = Soft) }
                TextButton(onClick = { Updates.closeDialog() }) { Text("Close", color = Soft) }
            }
        },
    )
}
