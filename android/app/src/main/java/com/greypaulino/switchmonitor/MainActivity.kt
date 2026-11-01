package com.greypaulino.switchmonitor

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import com.greypaulino.switchmonitor.data.Phase
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.ui.ConnectScreen
import com.greypaulino.switchmonitor.ui.ControlScreen
import com.greypaulino.switchmonitor.ui.theme.SwitchMonitorTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Remote.init(this)
        setContent {
            SwitchMonitorTheme {
                val ui by Remote.ui.collectAsState()
                val state = ui.state
                when {
                    ui.phase == Phase.Connected && state != null -> ControlScreen(ui, state)
                    // A known computer: the control screen right away (last state kept, or placeholders), live once it answers.
                    ui.host != null && ui.phase in setOf(Phase.Connecting, Phase.Offline, Phase.Connected) ->
                        ControlScreen(ui, state ?: Remote.latest, live = false)
                    else -> ConnectScreen(ui)
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        Remote.start()
    }

    override fun onStop() {
        super.onStop()
        Remote.stop()
    }
}
