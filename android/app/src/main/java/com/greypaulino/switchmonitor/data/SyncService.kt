package com.greypaulino.switchmonitor.data

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.greypaulino.switchmonitor.MainActivity
import com.greypaulino.switchmonitor.R

/**
 * Keeps the phone connected to the group while the app is in the background, so what is copied on a computer (and the files sent to
 * this phone) still arrives. It runs only while Shared clipboard is on, and shows a quiet notification, as Android requires.
 * (Android does not let a background app READ the clipboard, so what is copied on the phone goes out only while the app is open.)
 */
class SyncService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(NotificationChannel(CHANNEL, "Shared clipboard", NotificationManager.IMPORTANCE_MIN))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.g_monitor)
            .setContentTitle("SwitchMonitor")
            .setContentText("Shared clipboard is on")
            .setContentIntent(open).setOngoing(true).setPriority(NotificationCompat.PRIORITY_MIN).build()
        if (Build.VERSION.SDK_INT >= 34) startForeground(ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(ID, notification)
        Remote.init(this)
        Remote.hold("service")
        return START_STICKY
    }

    override fun onDestroy() {
        Remote.release("service")
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "sync"
        private const val ID = 4701

        /** Starts or stops the service to match the Shared clipboard switch. */
        fun update(context: Context, on: Boolean) {
            val intent = Intent(context, SyncService::class.java)
            try {
                if (on) ContextCompat.startForegroundService(context, intent) else context.stopService(intent)
            } catch (_: Exception) { }   // started from the background where Android does not allow it: the next time the app opens it starts
        }
    }
}
