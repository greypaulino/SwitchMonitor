package com.greypaulino.switchmonitor.surfaces

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.view.View
import android.widget.RemoteViews
import com.greypaulino.switchmonitor.MainActivity
import com.greypaulino.switchmonitor.R
import com.greypaulino.switchmonitor.data.Display
import com.greypaulino.switchmonitor.data.PcState
import com.greypaulino.switchmonitor.data.Profile
import com.greypaulino.switchmonitor.data.Remote
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.json.JSONObject

// The four home screen widgets. Android widgets can't be dragged, so a brightness bar is 20 blocks of 5 % each: a tap
// sets that level. Tapping the percentage opens the quick panel, where the blocks do follow a finger.

class LargeWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = Widgets.update(context, manager, ids, Widgets.Kind.Large)
}

class SmallWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = Widgets.update(context, manager, ids, Widgets.Kind.Small)
}

class ProfilesWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = Widgets.update(context, manager, ids, Widgets.Kind.Profiles)
}

object Widgets {
    enum class Kind(val layout: Int, val provider: Class<*>) {
        Large(R.layout.widget_large, LargeWidget::class.java),
        Small(R.layout.widget_small, SmallWidget::class.java),
        Profiles(R.layout.widget_profiles, ProfilesWidget::class.java),
    }

    private val blockIds = intArrayOf(
        R.id.b0, R.id.b1, R.id.b2, R.id.b3, R.id.b4, R.id.b5, R.id.b6, R.id.b7, R.id.b8, R.id.b9,
        R.id.b10, R.id.b11, R.id.b12, R.id.b13, R.id.b14, R.id.b15, R.id.b16, R.id.b17, R.id.b18, R.id.b19,
    )
    private val badge = arrayOf(
        intArrayOf(R.id.p0, R.id.pc0, R.id.pr0, R.id.pi0, R.id.pt0),
        intArrayOf(R.id.p1, R.id.pc1, R.id.pr1, R.id.pi1, R.id.pt1),
        intArrayOf(R.id.p2, R.id.pc2, R.id.pr2, R.id.pi2, R.id.pt2),
        intArrayOf(R.id.p3, R.id.pc3, R.id.pr3, R.id.pi3, R.id.pt3),
        intArrayOf(R.id.p4, R.id.pc4, R.id.pr4, R.id.pi4, R.id.pt4),
    )
    private val tileNames = intArrayOf(R.id.pn0, R.id.pn1, R.id.pn2, R.id.pn3)

    /** Asks the launcher to place a widget (it shows its own confirmation); older launchers: how to add it by hand. */
    fun pin(context: Context, kind: Kind) {
        val manager = AppWidgetManager.getInstance(context)
        if (android.os.Build.VERSION.SDK_INT >= 26 && manager.isRequestPinAppWidgetSupported) {
            manager.requestPinAppWidget(ComponentName(context, kind.provider), null, null)
        } else {
            android.widget.Toast.makeText(context, "Long-press the home screen › Widgets › SwitchMonitor", android.widget.Toast.LENGTH_LONG).show()
        }
    }

    /** Redraws every placed widget (after a state change). */
    fun updateAll(context: Context) {
        val manager = AppWidgetManager.getInstance(context)
        for (kind in Kind.entries) {
            val ids = manager.getAppWidgetIds(ComponentName(context, kind.provider))
            if (ids.isNotEmpty()) update(context, manager, ids, kind)
        }
    }

    fun update(context: Context, manager: AppWidgetManager, ids: IntArray, kind: Kind) {
        Remote.init(context)
        val views = build(context, kind)
        for (id in ids) manager.updateAppWidget(id, views)
    }

    /** The widget as it looks now (also the previews in the app's "Add a widget"). */
    fun build(context: Context, kind: Kind): RemoteViews {
        val v = RemoteViews(context.packageName, kind.layout)
        val state = Remote.latest
        val ready = Remote.isPaired && state != null
        v.setViewVisibility(R.id.empty, if (ready) View.GONE else View.VISIBLE)
        v.setViewVisibility(R.id.content, if (ready) View.VISIBLE else View.GONE)
        if (!ready) {
            v.setOnClickPendingIntent(R.id.root, openApp(context))
            return v
        }
        state!!
        if (kind == Kind.Profiles) {
            fillTiles(context, v, state)
            return v
        }
        val display = Controls.target(context, state)
        val value = display?.brightness
        v.setTextViewText(R.id.name, Controls.label(state, display))
        v.setTextViewText(R.id.pct, if (value == null) "–" else "$value%")
        v.setOnClickPendingIntent(R.id.pct, Controls.panel(context))
        if (kind == Kind.Large) {
            val lit = ((value ?: 0) + 2) / 5
            blockIds.forEachIndexed { i, id ->
                v.setInt(id, "setBackgroundResource", if (i < lit) R.drawable.w_block_on else R.drawable.w_block_off)
                if (display != null) v.setOnClickPendingIntent(id, Controls.set(context, display.id, (i + 1) * 5, 100 + i))
            }
        }
        if (kind == Kind.Large) {
            v.setInt(R.id.dot, "setColorFilter", if (Remote.connected) 0xFF4CD97B.toInt() else 0xFF8A8A8A.toInt())
            v.setOnClickPendingIntent(R.id.name_box, Controls.nextDisplay(context))
            fillBadges(context, v, state.profiles)
        }
        if (kind == Kind.Small && display != null) {
            v.setOnClickPendingIntent(R.id.minus, Controls.step(context, display.id, -5, 1))
            v.setOnClickPendingIntent(R.id.plus, Controls.step(context, display.id, 5, 2))
            v.setInt(R.id.plus_icon, "setColorFilter", 0xFF141414.toInt())
            // Switch button: the widget shows and controls the other display (hidden with one display or linked).
            val several = state.displays.count { it.on } > 1 && !state.linked
            v.setViewVisibility(R.id.swap, if (several) View.VISIBLE else View.GONE)
            v.setOnClickPendingIntent(R.id.swap, Controls.nextDisplay(context))
            v.setImageViewResource(R.id.kind, if (display.isLaptop) R.drawable.g_laptop else R.drawable.g_monitor)
        }
        return v
    }

    private fun fillBadges(context: Context, v: RemoteViews, profiles: List<Profile>) {
        badge.forEachIndexed { i, ids ->
            val p = profiles.getOrNull(i)
            v.setViewVisibility(ids[0], if (p == null) View.INVISIBLE else View.VISIBLE)
            if (p == null) return@forEachIndexed
            v.setInt(ids[1], "setColorFilter", p.color.toInt())
            v.setViewVisibility(ids[2], if (p.active) View.VISIBLE else View.INVISIBLE)
            icon(v, ids[3], ids[4], p)
            v.setOnClickPendingIntent(ids[0], Controls.profile(context, p.name, 200 + i))
        }
    }

    private fun fillTiles(context: Context, v: RemoteViews, state: PcState) {
        // Up to four: the active profile always among them.
        val chosen = state.profiles.take(4).toMutableList()
        state.profiles.firstOrNull { it.active }?.let { if (it !in chosen && chosen.isNotEmpty()) chosen[chosen.size - 1] = it }
        for (i in 0 until 4) {
            val ids = badge[i]
            val p = chosen.getOrNull(i)
            v.setViewVisibility(ids[0], if (p == null) View.INVISIBLE else View.VISIBLE)
            if (p == null) continue
            v.setInt(ids[1], "setColorFilter", p.color.toInt())
            v.setViewVisibility(ids[2], if (p.active) View.VISIBLE else View.INVISIBLE)
            icon(v, ids[3], ids[4], p)
            v.setTextViewText(tileNames[i], p.name)
            v.setOnClickPendingIntent(ids[0], Controls.profile(context, p.name, 300 + i))
        }
    }

    private fun icon(v: RemoteViews, iconId: Int, letterId: Int, p: Profile) {
        val res = Controls.profileIcon(p)
        if (res != 0) {
            v.setViewVisibility(iconId, View.VISIBLE)
            v.setViewVisibility(letterId, View.GONE)
            v.setImageViewResource(iconId, res)
            v.setInt(iconId, "setColorFilter", 0xFF141414.toInt())
        } else {
            v.setViewVisibility(iconId, View.GONE)
            v.setViewVisibility(letterId, View.VISIBLE)
            v.setTextViewText(letterId, p.name.take(1).uppercase())
        }
    }

    private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
        context, 1, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )
}

/** What a widget or the notification does when tapped; shared by both. */
object Controls {
    const val SET = "com.greypaulino.switchmonitor.SET"
    const val STEP = "com.greypaulino.switchmonitor.STEP"
    const val NEXT = "com.greypaulino.switchmonitor.NEXT_DISPLAY"
    const val PROFILE = "com.greypaulino.switchmonitor.PROFILE"

    private fun broadcast(context: Context, code: Int, action: String, fill: Intent.() -> Unit): PendingIntent =
        PendingIntent.getBroadcast(
            context, code, Intent(context, ControlReceiver::class.java).setAction(action).apply(fill),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

    fun set(context: Context, display: String, value: Int, code: Int) =
        broadcast(context, code, SET) { putExtra("display", display); putExtra("value", value) }

    fun step(context: Context, display: String, delta: Int, code: Int) =
        broadcast(context, code, STEP) { putExtra("display", display); putExtra("delta", delta) }

    fun nextDisplay(context: Context) = broadcast(context, 3, NEXT) { }

    fun profile(context: Context, name: String, code: Int) = broadcast(context, code, PROFILE) { putExtra("name", name) }

    fun panel(context: Context): PendingIntent = PendingIntent.getActivity(
        context, 4, Intent(context, QuickPanelActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    /** The display the widgets act on: the one picked with the name button, else the computer's selected one. */
    fun target(context: Context, state: PcState): Display? {
        val on = state.displays.filter { it.on }
        if (state.linked) return on.firstOrNull { it.ready } ?: on.firstOrNull()
        val picked = prefs(context).getString("widgetDisplay", null)
        return on.firstOrNull { it.id == picked } ?: on.firstOrNull { it.selected } ?: on.firstOrNull()
    }

    fun label(state: PcState, display: Display?): String = when {
        display == null -> "No display"
        state.linked && state.displays.count { it.on } > 1 -> "All displays (linked)"
        else -> display.name
    }

    fun prefs(context: Context) = context.getSharedPreferences("switchmonitor", Context.MODE_PRIVATE)

    fun profileIcon(p: Profile): Int = if (p.custom) R.drawable.g_custom else when (p.icon) {
        "work" -> R.drawable.g_work
        "night" -> R.drawable.g_night
        "game" -> R.drawable.g_game
        "movie", "video" -> R.drawable.g_movie
        "photo", "picture" -> R.drawable.g_photo
        "reading", "library", "document", "school" -> R.drawable.g_reading
        "star" -> R.drawable.g_star
        "home" -> R.drawable.g_home
        "music", "sound" -> R.drawable.g_music
        "headphones" -> R.drawable.g_headphones
        "sun" -> R.drawable.g_sun
        "bulb" -> R.drawable.g_bulb
        "eye" -> R.drawable.g_eye
        "monitor" -> R.drawable.g_monitor
        "laptop" -> R.drawable.g_laptop
        else -> 0
    }
}

class ControlReceiver : BroadcastReceiver() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onReceive(context: Context, intent: Intent) {
        Remote.init(context)
        val state = Remote.latest ?: return
        when (intent.action) {
            Controls.SET, Controls.STEP -> {
                val id = intent.getStringExtra("display") ?: return
                val now = state.displays.firstOrNull { it.id == id }?.brightness ?: return
                val value = if (intent.action == Controls.SET) intent.getIntExtra("value", now)
                else (((now + intent.getIntExtra("delta", 0)) + 2) / 5 * 5)
                val v = value.coerceIn(0, 100)
                Remote.guess(id, v)
                send(JSONObject().put("type", "brightness").put("display", id).put("value", v))
            }
            Controls.PROFILE -> {
                val name = intent.getStringExtra("name") ?: return
                Remote.guessProfile(name)
                send(JSONObject().put("type", "profile").put("name", name))
            }
            Controls.NEXT -> {
                val on = state.displays.filter { it.on }
                if (on.size < 2 || state.linked) return
                val current = Controls.target(context, state)
                val next = on[(on.indexOf(current) + 1) % on.size]
                Controls.prefs(context).edit().putString("widgetDisplay", next.id).apply()
                Widgets.updateAll(context)
            }
        }
    }

    /** Keeps the receiver alive until the command is out (the app may not be running). */
    private fun send(message: JSONObject) {
        val done = goAsync()
        Remote.background(message)
        scope.launch {
            try { Remote.flushed() } finally { done.finish() }
        }
    }
}
