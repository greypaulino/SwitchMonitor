package com.greypaulino.switchmonitor

import android.app.Application
import com.greypaulino.switchmonitor.data.Remote
import com.greypaulino.switchmonitor.surfaces.QuickTile
import com.greypaulino.switchmonitor.surfaces.Widgets

class SwitchMonitorApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Remote.init(this)
        // "Controls in notifications" (0.2 test builds) was dropped: clear what it left behind.
        if (android.os.Build.VERSION.SDK_INT >= 26) try {
            getSystemService(android.app.NotificationManager::class.java)?.apply { cancel(47820); deleteNotificationChannel("display-controls"); deleteNotificationChannel("controls") }
        } catch (_: Exception) { }
        // Widgets and the tile follow the computer's state.
        Remote.onChange {
            Widgets.updateAll(this)
            try { QuickTile.refresh(this) } catch (_: Throwable) { }
        }
    }
}
