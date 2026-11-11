package com.greypaulino.switchmonitor

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import com.greypaulino.switchmonitor.data.Phase
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.data.Transfers
import com.greypaulino.switchmonitor.ui.ConnectScreen
import com.greypaulino.switchmonitor.ui.ControlScreen
import com.greypaulino.switchmonitor.ui.theme.SwitchMonitorTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Remote.init(this)
        // Android 13+ asks once before showing notifications (files received, the clipboard service).
        if (android.os.Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            val prefs = getSharedPreferences("switchmonitor", MODE_PRIVATE)
            if (!prefs.getBoolean("askedNotifications", false)) {
                prefs.edit().putBoolean("askedNotifications", true).apply()
                requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 7)
            }
        }
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

    // What is copied while the app is open goes to the group (when Shared clipboard is on).
    private val clipListener = android.content.ClipboardManager.OnPrimaryClipChangedListener {
        val manager = getSystemService(CLIPBOARD_SERVICE) as android.content.ClipboardManager
        val clip = try { manager.primaryClip?.takeIf { it.itemCount > 0 } } catch (_: Exception) { null }
        // What an app marks as sensitive (a password manager's copy) is never shared.
        if (clip?.description?.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true) return@OnPrimaryClipChangedListener
        val uri = clip?.getItemAt(0)?.uri
        if (clip != null && uri != null && (0 until clip.description.mimeTypeCount).any { clip.description.getMimeType(it).startsWith("image/") }) {
            // An image: it goes as an image, never as the text of its address.
            Transfers.sendImage(this, uri)
            return@OnPrimaryClipChangedListener
        }
        val text = try { clip?.getItemAt(0)?.coerceToText(this)?.toString() } catch (_: Exception) { null }
        if (!text.isNullOrEmpty() && uri == null) Remote.clipboardChanged(text)
    }

    override fun onStart() {
        super.onStart()
        Remote.start()
        (getSystemService(CLIPBOARD_SERVICE) as android.content.ClipboardManager).addPrimaryClipChangedListener(clipListener)
    }

    override fun onStop() {
        super.onStop()
        (getSystemService(CLIPBOARD_SERVICE) as android.content.ClipboardManager).removePrimaryClipChangedListener(clipListener)
        Remote.stop()
    }
}
