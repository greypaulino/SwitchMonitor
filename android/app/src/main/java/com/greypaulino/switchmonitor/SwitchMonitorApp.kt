package com.greypaulino.switchmonitor

import android.app.Application
import com.greypaulino.switchmonitor.data.Remote

class SwitchMonitorApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Remote.init(this)
    }
}
