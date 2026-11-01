package com.greypaulino.switchmonitor.surfaces

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.annotation.RequiresApi
import com.greypaulino.switchmonitor.R
import com.greypaulino.switchmonitor.data.Remote

/** The SwitchMonitor tile in Quick Settings: shows the level and profile, and opens the quick panel. */
@RequiresApi(24)
class QuickTile : TileService() {
    override fun onStartListening() {
        super.onStartListening()
        Remote.init(this)
        val tile = qsTile ?: return
        val state = Remote.latest
        val d = state?.let { Controls.target(this, it) }
        tile.label = "SwitchMonitor"
        val sub = when {
            !Remote.isPaired || state == null -> "Not connected"
            else -> listOfNotNull(d?.brightness?.let { "$it%" }, state.profiles.firstOrNull { it.active }?.name).joinToString(" · ")
        }
        if (Build.VERSION.SDK_INT >= 29) tile.subtitle = sub else tile.label = "SwitchMonitor · $sub"
        tile.icon = Icon.createWithResource(this, R.drawable.g_monitor)
        tile.state = if (Remote.isPaired && state != null) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.updateTile()
    }

    override fun onClick() {
        super.onClick()
        tappedAt = android.os.SystemClock.uptimeMillis()
        Remote.init(this)
        if (isLocked) unlockAndRun { openPanel() } else openPanel()
    }

    /** The panel as a dialog over the shade (fast, nothing to start); the full-screen activity only if that fails. */
    private fun openPanel() {
        try {
            showDialog(QuickPanelDialog(this))
        } catch (e: RuntimeException) {
            android.util.Log.w("switchmonitor", "Quick panel dialog failed, opening the activity", e)
            openActivity()
        }
    }

    @SuppressLint("StartActivityAndCollapseDeprecated")
    private fun openActivity() {
        val intent = Intent(this, QuickPanelActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 6, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }

    companion object {
        /** When the tile was tapped (uptime ms), so the panel can log how long it took to show. */
        @Volatile var tappedAt = 0L

        fun refresh(context: Context) {
            if (Build.VERSION.SDK_INT >= 24) requestListeningState(context, ComponentName(context, QuickTile::class.java))
        }
    }
}
