package com.greypaulino.switchmonitor.data

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

enum class Phase {
    /** No computer chosen yet: the search screen. */
    Choose,
    /** Opening the connection to the chosen computer. */
    Connecting,
    /** Connected, but this phone is not paired with that computer: type the code. */
    NeedCode,
    /** Connected and paired: the control screen. */
    Connected,
    /** The chosen computer can't be reached right now (it retries on its own). */
    Offline,
}

data class RemoteUi(
    val phase: Phase = Phase.Choose,
    val host: String? = null,
    val computer: String? = null,
    val state: PcState? = null,
    val found: List<Found> = emptyList(),
    /** Computers this phone is paired with (its token is kept), newest first. */
    val paired: List<Found> = emptyList(),
    val searching: Boolean = false,
    val message: String? = null,
)

/**
 * The one connection to the SwitchMonitor computer, shared by the app (and later the widgets, notification and tile).
 * Protocol: ws://<computer>:47820/ws, JSON both ways; see RemoteServer/PhoneBridge on the PC.
 */
object Remote {
    const val DEFAULT_PORT = 47820

    private lateinit var appContext: Context
    private lateinit var prefs: SharedPreferences
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val client = OkHttpClient.Builder()
        .connectTimeout(4, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.SECONDS)
        .pingInterval(15, TimeUnit.SECONDS)
        .build()

    private val _ui = MutableStateFlow(RemoteUi())
    val ui: StateFlow<RemoteUi> = _ui

    @Volatile private var socket: WebSocket? = null
    @Volatile private var wanted = false
    private var retry: Job? = null
    private var messageClear: Job? = null

    fun init(context: Context) {
        if (::appContext.isInitialized) return
        appContext = context.applicationContext
        prefs = appContext.getSharedPreferences("switchmonitor", Context.MODE_PRIVATE)
        // 0.1 kept one token for the current computer only.
        prefs.getString("token", null)?.let { old ->
            val host = prefs.getString("host", null)
            if (host != null) keep(prefs.getString("computer", null) ?: host, host, prefs.getInt("port", DEFAULT_PORT), old)
            prefs.edit().remove("token").apply()
        }
        _ui.update { it.copy(host = prefs.getString("host", null), computer = prefs.getString("computer", null), paired = pairedList()) }
    }

    private val savedHost get() = prefs.getString("host", null)
    private val savedPort get() = prefs.getInt("port", DEFAULT_PORT)

    // ---- Paired computers: name, last address and token of each, so switching computers never needs a new code ----

    private fun pairedJson(): org.json.JSONArray =
        try { org.json.JSONArray(prefs.getString("computers", "[]")) } catch (_: Exception) { org.json.JSONArray() }

    private fun pairedList(): List<Found> {
        val a = pairedJson()
        return (0 until a.length()).mapNotNull { a.optJSONObject(it) }
            .map { Found(it.optString("name"), it.optString("host"), it.optInt("port", DEFAULT_PORT)) }
    }

    /** The token for this computer: found by its name (its address can change), else by its address. */
    private fun tokenFor(name: String?, host: String): String? {
        val a = pairedJson()
        val all = (0 until a.length()).mapNotNull { a.optJSONObject(it) }
        val entry = all.firstOrNull { name != null && it.optString("name").equals(name, ignoreCase = true) }
            ?: all.firstOrNull { it.optString("host") == host }
        return entry?.optString("token")?.takeIf { it.isNotEmpty() }
    }

    /** Remembers (or refreshes) a paired computer; a null token keeps the one already saved. */
    private fun keep(name: String, host: String, port: Int, token: String?) {
        val a = pairedJson()
        val rest = org.json.JSONArray()
        var oldToken: String? = null
        for (i in 0 until a.length()) {
            val o = a.optJSONObject(i) ?: continue
            if (o.optString("name").equals(name, ignoreCase = true) || o.optString("host") == host) oldToken = oldToken ?: o.optString("token")
            else rest.put(o)
        }
        val t = token ?: oldToken ?: return
        val list = org.json.JSONArray().put(JSONObject().put("name", name).put("host", host).put("port", port).put("token", t))
        for (i in 0 until rest.length()) list.put(rest.get(i))
        prefs.edit().putString("computers", list.toString()).apply()
        _ui.update { it.copy(paired = pairedList()) }
    }

    private fun dropToken(name: String?, host: String) {
        val a = pairedJson()
        val list = org.json.JSONArray()
        for (i in 0 until a.length()) {
            val o = a.optJSONObject(i) ?: continue
            if ((name != null && o.optString("name").equals(name, ignoreCase = true)) || o.optString("host") == host) continue
            list.put(o)
        }
        prefs.edit().putString("computers", list.toString()).apply()
        _ui.update { it.copy(paired = pairedList()) }
    }

    /** No longer paired with this computer (from the search screen); the computer keeps it until removed there. */
    fun unpair(pc: Found) = dropToken(pc.name, pc.host)

    // ---- Lifecycle: connected while something is showing the controls ----

    fun start() {
        wanted = true
        val host = savedHost
        if (host == null) {
            _ui.update { it.copy(phase = Phase.Choose) }
            if (_ui.value.found.isEmpty()) search()
        } else if (socket == null) {
            open(host, savedPort)
        }
    }

    fun stop() {
        wanted = false
        retry?.cancel()
        socket?.close(1000, null)
        socket = null
    }

    // ---- Choosing a computer ----

    fun search() {
        if (_ui.value.searching) return
        _ui.update { it.copy(searching = true) }
        scope.launch {
            val found = Discovery.search(appContext)
            _ui.update { it.copy(found = found, searching = false) }
        }
    }

    /** Connect to a computer from the search, or typed by hand ("10.0.0.45" or "10.0.0.45:47820"). */
    fun choose(host: String, port: Int = DEFAULT_PORT, name: String? = null) {
        val clean = host.trim().removePrefix("http://").removeSuffix("/")
        val (h, p) = clean.split(':').let { if (it.size == 2) it[0] to (it[1].toIntOrNull() ?: port) else clean to port }
        if (h.isEmpty()) return
        val sameComputer = h == savedHost
        val known = name ?: pairedList().firstOrNull { it.host == h }?.name
        val edit = prefs.edit().putString("host", h).putInt("port", p)
        if (known != null) edit.putString("computer", known) else if (!sameComputer) edit.remove("computer")
        edit.apply()
        wanted = true
        socket?.close(1000, null)
        socket = null
        _ui.update { it.copy(host = h, computer = known ?: it.computer.takeIf { sameComputer }, state = null) }
        open(h, p)
    }

    /** Back to the search screen. The pairing is kept, so coming back to this computer needs no code. */
    fun forget() {
        stop()
        prefs.edit().remove("host").remove("computer").apply()
        _ui.update { RemoteUi(phase = Phase.Choose, found = it.found, paired = pairedList()) }
        wanted = true
        search()
    }

    fun pair(code: String) {
        val name = listOfNotNull(Build.MANUFACTURER?.replaceFirstChar { it.uppercase() }, Build.MODEL)
            .joinToString(" ").ifBlank { "Android phone" }
        send(JSONObject().put("type", "pair").put("code", code.trim()).put("name", name))
    }

    // ---- Commands ----

    private val pendingBrightness = ConcurrentHashMap<String, Int>()
    private var brightnessJob: Job? = null

    /** Slider moves arrive many times a second; the latest value per display goes out every 70 ms. */
    fun brightness(display: String, value: Int) {
        pendingBrightness[display] = value.coerceIn(0, 100)
        if (brightnessJob?.isActive == true) return
        brightnessJob = scope.launch {
            while (pendingBrightness.isNotEmpty()) {
                for (key in pendingBrightness.keys.toList()) {
                    val v = pendingBrightness.remove(key) ?: continue
                    send(JSONObject().put("type", "brightness").put("display", key).put("value", v))
                }
                delay(70)
            }
        }
    }

    fun select(display: String) = send(JSONObject().put("type", "select").put("display", display))
    fun link(on: Boolean) = send(JSONObject().put("type", "link").put("on", on))
    fun profile(name: String) = send(JSONObject().put("type", "profile").put("name", name))
    fun mode(value: Int) = send(JSONObject().put("type", "mode").put("value", value))
    fun input(display: String, code: Int) = send(JSONObject().put("type", "input").put("display", display).put("code", code))
    fun cycle(display: String, code: Int, on: Boolean) =
        send(JSONObject().put("type", "cycle").put("display", display).put("code", code).put("on", on))
    fun main(display: String) = send(JSONObject().put("type", "main").put("display", display))
    fun preferred(display: String) = send(JSONObject().put("type", "preferred").put("display", display))

    private fun send(message: JSONObject) {
        val s = socket
        if (s == null || !s.send(message.toString())) show("Not connected to the computer.")
    }

    // ---- The WebSocket ----

    private fun open(host: String, port: Int) {
        retry?.cancel()
        _ui.update { it.copy(phase = if (it.phase == Phase.Offline) Phase.Offline else Phase.Connecting, host = host) }
        val request = Request.Builder().url("http://$host:$port/ws").build()
        socket = client.newWebSocket(request, Listener())
    }

    private class Listener : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (webSocket !== socket) return
            val t = savedHost?.let { tokenFor(prefs.getString("computer", null), it) }
            if (t != null) webSocket.send(JSONObject().put("type", "hello").put("token", t).toString())
            else _ui.update { it.copy(phase = Phase.NeedCode) }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (webSocket !== socket) return
            val m = try { JSONObject(text) } catch (_: Exception) { return }
            when (m.optString("type")) {
                "paired" -> {
                    prefs.edit().putString("computer", m.optString("computer")).apply()
                    savedHost?.let { keep(m.optString("computer").ifBlank { it }, it, savedPort, m.optString("token")) }
                    _ui.update { it.copy(phase = Phase.Connected, computer = m.optString("computer"), message = null) }
                }
                "welcome" -> {
                    prefs.edit().putString("computer", m.optString("computer")).apply()
                    savedHost?.let { keep(m.optString("computer").ifBlank { it }, it, savedPort, null) }   // its address may have changed
                    _ui.update { it.copy(phase = Phase.Connected, computer = m.optString("computer")) }
                }
                "state" -> {
                    val state = try { parseState(m) } catch (_: Exception) { return }
                    _ui.update { it.copy(state = state, computer = state.computer.ifBlank { it.computer }) }
                }
                "error" -> {
                    if (m.optString("code") == "unpaired") {
                        savedHost?.let { dropToken(prefs.getString("computer", null), it) }
                        _ui.update { it.copy(phase = Phase.NeedCode, state = null) }
                    }
                    show(m.optString("error"))
                }
            }
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost(webSocket)

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = lost(webSocket)

        private fun lost(webSocket: WebSocket) {
            if (webSocket !== socket) return
            socket = null
            if (!wanted) return
            _ui.update { it.copy(phase = Phase.Offline) }
            retry?.cancel()
            retry = scope.launch {
                delay(3000)
                val host = savedHost
                if (wanted && socket == null && host != null) open(host, savedPort)
            }
        }
    }

    private fun show(text: String) {
        if (text.isBlank()) return
        _ui.update { it.copy(message = text) }
        messageClear?.cancel()
        messageClear = scope.launch {
            delay(4000)
            _ui.update { if (it.message == text) it.copy(message = null) else it }
        }
    }
}
