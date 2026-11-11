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
    /** This phone belongs to "My devices" (the group of the person's computers and phones). */
    val inGroup: Boolean = false,
    /** The computers of that group (their address is found by the search). */
    val group: List<Found> = emptyList(),
    /** Waiting for the computer's person to allow this phone into the group. */
    val enrolling: Boolean = false,
    /** The device id of the computer this phone is connected through (a member of the group). */
    val gateway: String = "",
    /** The computer of the group the mouse and keyboard act on ("" = the one connected). */
    val target: String = "",
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
    // Small messages (a pointer step) go out at once instead of waiting to be bundled with the next one (Nagle's algorithm).
    private val noDelay = object : javax.net.SocketFactory() {
        private val base = javax.net.SocketFactory.getDefault()
        private fun <T : java.net.Socket> T.fast(): T = apply { tcpNoDelay = true }
        override fun createSocket() = base.createSocket().fast()
        override fun createSocket(host: String?, port: Int) = base.createSocket(host, port).fast()
        override fun createSocket(host: String?, port: Int, local: java.net.InetAddress?, localPort: Int) = base.createSocket(host, port, local, localPort).fast()
        override fun createSocket(host: java.net.InetAddress?, port: Int) = base.createSocket(host, port).fast()
        override fun createSocket(address: java.net.InetAddress?, port: Int, local: java.net.InetAddress?, localPort: Int) = base.createSocket(address, port, local, localPort).fast()
    }
    private val client = OkHttpClient.Builder()
        .socketFactory(noDelay)
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
        MyDevices.init(appContext)
        // 0.1 kept one token for the current computer only.
        prefs.getString("token", null)?.let { old ->
            val host = prefs.getString("host", null)
            if (host != null) keep(prefs.getString("computer", null) ?: host, host, prefs.getInt("port", DEFAULT_PORT), old)
            prefs.edit().remove("token").apply()
        }
        val host = prefs.getString("host", null)
        // A known computer starts as Connecting, so the first frame is already the control screen (not the search).
        _ui.update { it.copy(phase = if (host != null) Phase.Connecting else it.phase, host = host, computer = prefs.getString("computer", null), paired = pairedList(), inGroup = MyDevices.inGroup(), group = groupList()) }
        latest = prefs.getString("lastState", null)?.let { try { parseState(JSONObject(it)) } catch (_: Exception) { null } }
    }

    private val savedHost get() = prefs.getString("host", null)
    private val savedPort get() = prefs.getInt("port", DEFAULT_PORT)

    // ---- Paired computers: name, last address and token of each, so switching computers never needs a new code ----

    /** The paired computers with their tokens, sealed with the Keystore (see Vault); older plain lists are sealed on read. */
    private fun pairedJson(): org.json.JSONArray {
        val text = prefs.getString("computersSealed", null)?.let { Vault.open(it) }
            ?: prefs.getString("computers", null)?.also { plain -> saveComputers(plain) }
            ?: "[]"
        return try { org.json.JSONArray(text) } catch (_: Exception) { org.json.JSONArray() }
    }

    private fun saveComputers(json: String) {
        val sealed = Vault.seal(json) ?: return
        prefs.edit().putString("computersSealed", sealed).remove("computers").apply()
    }

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
        saveComputers(list.toString())
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
        saveComputers(list.toString())
        _ui.update { it.copy(paired = pairedList()) }
    }

    /** No longer paired with this computer (from the search screen); the computer keeps it until removed there. */
    fun unpair(pc: Found) = dropToken(pc.name, pc.host)

    // ---- Lifecycle: connected while something needs it (the app on screen, the notification, the quick panel, a
    // widget's tap for a few seconds). Each of them holds the connection by a name and lets it go when done. ----

    private val holders = mutableSetOf<String>()

    /** The app came to the front. */
    fun start() {
        if (savedHost == null) {
            _ui.update { it.copy(phase = Phase.Choose) }
            if (_ui.value.found.isEmpty()) search()
        }
        hold("app")
    }

    /** The app went to the background. */
    fun stop() = release("app")

    @Synchronized
    fun hold(who: String) {
        holders += who
        wanted = true
        val host = savedHost
        if (host != null && socket == null) open(host, savedPort)
    }

    @Synchronized
    fun release(who: String) {
        holders -= who
        if (holders.isNotEmpty()) return
        wanted = false
        retry?.cancel()
        socket?.close(1000, null)
        socket = null
    }

    val isPaired: Boolean get() = savedHost != null && (MyDevices.inGroup() || savedHost?.let { tokenFor(prefs.getString("computer", null), it) } != null)

    // ---- My devices: this phone in the group of the person's computers and phones ----

    private fun groupList(): List<Found> = MyDevices.computers().map { Found(it.name, "", DEFAULT_PORT, it.deviceId) }

    private fun refreshGroup() = _ui.update { it.copy(inGroup = MyDevices.inGroup(), group = groupList(), enrolling = false) }

    /** Leaves the group on this phone only; the computers keep it (and the phone can be taken out there). */
    fun leaveGroup() {
        MyDevices.leave()
        refreshGroup()
    }

    @Volatile private var challenge = ""
    @Volatile private var memberNonce: String? = null
    @Volatile private var enrollCode: String? = null

    private fun deviceName(): String = listOfNotNull(Build.MANUFACTURER?.replaceFirstChar { it.uppercase() }, Build.MODEL)
        .joinToString(" ").ifBlank { "Android phone" }

    /** Joins the group of the computer this phone is connected to, with the code its Settings > Devices > My devices shows. */
    fun enroll(code: String) {
        val s = socket
        if (s == null || challenge.isEmpty()) { show("Not connected to the computer."); return }
        val clean = code.trim()
        enrollCode = clean
        _ui.update { it.copy(enrolling = true) }
        s.send(JSONObject().put("type", "enroll").put("deviceId", MyDevices.deviceId).put("name", deviceName()).put("kind", "phone")
            .put("key", MyDevices.publicKey).put("proof", MyDevices.enrollProof(clean, challenge)).toString())
    }

    // ---- What the widgets, the notification and the tile show: the last state seen, kept across restarts ----

    @Volatile var latest: PcState? = null
        private set
    private val listeners = java.util.concurrent.CopyOnWriteArrayList<() -> Unit>()
    private var notifyJob: Job? = null

    /** Called (at most every 200 ms) when the state, the connection or a widget's own guess changes. */
    fun onChange(listener: () -> Unit) { listeners += listener }

    fun removeListener(listener: () -> Unit) { listeners -= listener }

    private fun changed() {
        if (notifyJob?.isActive == true) return
        notifyJob = scope.launch { delay(200); for (l in listeners) try { l() } catch (_: Exception) { } }
    }

    val connected: Boolean get() = _ui.value.phase == Phase.Connected && socket != null

    // ---- Commands from outside the app (widgets, notification, tile) ----

    private val queue = java.util.concurrent.ConcurrentLinkedQueue<String>()
    private var burst: Job? = null

    /** Sends now when connected, else connects, sends once paired-in, and lets the connection go 10 s later. */
    fun background(message: JSONObject) {
        if (savedHost == null) return
        val text = message.toString()
        val s = socket
        if (!(_ui.value.phase == Phase.Connected && s != null && s.send(text))) queue.add(text)
        hold("burst")
        burst?.cancel()
        burst = scope.launch { delay(10_000); release("burst") }
    }

    /** Suspends until the queued commands went out (or 6 s passed): a widget's broadcast waits for it. */
    suspend fun flushed() {
        val until = System.currentTimeMillis() + 6000
        while (queue.isNotEmpty() && System.currentTimeMillis() < until) delay(100)
    }

    private fun flushQueue() {
        val s = socket ?: return
        while (true) { val t = queue.poll() ?: break; s.send(t) }
    }

    /** A widget shows the new level at once, before the computer answers. */
    fun guess(display: String, value: Int) {
        val st = latest ?: return
        latest = st.copy(displays = st.displays.map { if (it.id == display || (st.linked && it.on)) it.copy(brightness = value) else it })
        changed()
    }

    fun guessProfile(name: String) {
        val st = latest ?: return
        latest = st.copy(profiles = st.profiles.map { it.copy(active = it.name == name, modified = false) })
        changed()
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
        synchronized(this) {
            holders.retainAll(setOf("notification"))   // the notification follows whichever computer comes next
            wanted = false
            retry?.cancel()
            socket?.close(1000, null)
            socket = null
        }
        prefs.edit().remove("host").remove("computer").remove("lastState").apply()
        latest = null
        changed()
        _ui.update { RemoteUi(phase = Phase.Choose, found = it.found, paired = pairedList()) }
        holders += "app"
        wanted = true
        search()
    }

    fun pair(code: String) {
        val name = deviceName()
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

    private var pendingVolume: Int? = null
    private var volumeJob: Job? = null

    /** The computer's Windows volume; slider moves are thinned out like brightness. */
    fun volume(value: Int) {
        pendingVolume = value.coerceIn(0, 100)
        if (volumeJob?.isActive == true) return
        volumeJob = scope.launch {
            while (true) {
                val v = pendingVolume ?: break
                pendingVolume = null
                send(JSONObject().put("type", "volume").put("value", v))
                delay(70)
            }
        }
    }

    fun mute(on: Boolean) {
        latest?.let { st -> st.volume?.let { latest = st.copy(volume = it.copy(muted = on)); changed() } }
        send(JSONObject().put("type", "mute").put("on", on))
    }

    fun select(display: String) = send(JSONObject().put("type", "select").put("display", display))
    fun link(on: Boolean) = send(JSONObject().put("type", "link").put("on", on))
    fun profile(name: String) = send(JSONObject().put("type", "profile").put("name", name))
    fun mode(value: Int) = send(JSONObject().put("type", "mode").put("value", value))
    fun input(display: String, code: Int) = send(JSONObject().put("type", "input").put("display", display).put("code", code))
    fun cycle(display: String, code: Int, on: Boolean) =
        send(JSONObject().put("type", "cycle").put("display", display).put("code", code).put("on", on))
    // ---- The phone as the computer's mouse and keyboard (dropped, never queued, when not connected) ----

    private fun sendInput(op: JSONObject) {
        val s = socket
        val ui = _ui.value
        // Aimed at another computer of the group: the one connected passes it on.
        if (ui.target.isNotEmpty() && ui.target != ui.gateway) op.put("target", ui.target)
        if (s != null && ui.phase == Phase.Connected) s.send(op.put("type", "input").toString())
    }

    /** The computer of the group the touchpad and keyboard act on. */
    fun aim(deviceId: String) = _ui.update { it.copy(target = deviceId) }

    // Moves pile up here and go out together every 8 ms (about 125 a second): even steps instead of bursts, and never a flood.
    private val pendingX = java.util.concurrent.atomic.AtomicInteger()
    private val pendingY = java.util.concurrent.atomic.AtomicInteger()
    @Volatile private var pumping = false

    private fun flushMoves() {
        val x = pendingX.getAndSet(0)
        val y = pendingY.getAndSet(0)
        if (x != 0 || y != 0) sendInput(JSONObject().put("op", "move").put("dx", x).put("dy", y))
    }

    fun pointer(dx: Int, dy: Int) {
        pendingX.addAndGet(dx); pendingY.addAndGet(dy)
        if (pumping) return
        pumping = true
        scope.launch {
            try {
                var idle = 0
                while (idle < 60) {
                    delay(8)
                    if (pendingX.get() != 0 || pendingY.get() != 0) { idle = 0; flushMoves() } else idle++
                }
            } finally { pumping = false }
        }
    }

    // A click or key never overtakes the moves before it.
    fun button(name: String, down: Boolean) { flushMoves(); sendInput(JSONObject().put("op", "btn").put("b", name).put("down", down)) }
    fun click(name: String) { button(name, true); button(name, false) }
    fun wheel(delta: Int) = sendInput(JSONObject().put("op", "wheel").put("d", delta).put("h", false))
    fun key(code: Int, up: Boolean) = sendInput(JSONObject().put("op", "key").put("vk", code).put("up", up))
    fun text(value: String) = sendInput(JSONObject().put("op", "text").put("s", value))

    fun main(display: String) = send(JSONObject().put("type", "main").put("display", display))
    fun preferred(display: String) = send(JSONObject().put("type", "preferred").put("display", display))

    private fun send(message: JSONObject) {
        val s = socket
        if (s != null && _ui.value.phase == Phase.Connected && s.send(message.toString())) return
        // Still connecting (the quick panel just opened, a reconnect): queued and sent as soon as the computer answers.
        if (savedHost != null && message.optString("type") != "pair") { background(message); return }
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
        // The computer's answer to our own challenge, expected in "welcome" (see "challenge" below).
        @Volatile private var expected: String? = null

        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (webSocket !== socket) return
            // The computer speaks first with a challenge; without a token for it, the code is needed.
            if (!MyDevices.inGroup() && savedHost?.let { tokenFor(prefs.getString("computer", null), it) } == null) _ui.update { it.copy(phase = Phase.NeedCode) }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (webSocket !== socket) return
            val m = try { JSONObject(text) } catch (_: Exception) { return }
            when (m.optString("type")) {
                "challenge" -> {
                    challenge = m.optString("nonce")
                    if (MyDevices.inGroup()) {
                        // A member of the group signs the challenge with its key: no token, no code. It tells what it knows of the
                        // group, and checks the computer's answer the same way.
                        val mine = nonce()
                        memberNonce = mine
                        val known = MyDevices.export()
                        webSocket.send(JSONObject().put("type", "member").put("deviceId", MyDevices.deviceId).put("nonce", mine)
                            .put("signature", MyDevices.sign("member:$challenge:$mine:${MyDevices.deviceId}"))
                            .put("members", known.getJSONArray("members")).put("revoked", known.getJSONArray("revoked")).toString())
                        return
                    }
                    val t = savedHost?.let { tokenFor(prefs.getString("computer", null), it) } ?: return
                    signInWithToken(webSocket, t)
                }
                "memberWelcome" -> {
                    MyDevices.merge(m.optJSONArray("members"), m.optJSONArray("revoked"))
                    val theirs = m.optString("deviceId")
                    val mine = memberNonce
                    if (mine == null || !MyDevices.verify(theirs, "server:$mine:$challenge:$theirs", m.optString("signature"))) {
                        show("This computer could not prove it is in your group.")
                        webSocket.close(1000, null)
                        return
                    }
                    val name = m.optString("computer")
                    prefs.edit().putString("computer", name).apply()
                    _ui.update { it.copy(phase = Phase.Connected, computer = name, group = groupList(), gateway = theirs) }
                    flushQueue()
                }
                "enrolled" -> {
                    val code = enrollCode
                    val eco = m.optJSONObject("ecosystem")
                    if (code == null || eco == null) return
                    val answer = MyDevices.enrollAnswer(code, challenge, m.optString("deviceId"), eco.optString("id"))
                    if (!java.security.MessageDigest.isEqual(answer.toByteArray(), m.optString("answer").toByteArray())) {
                        show("That computer could not prove it showed the code.")
                        _ui.update { it.copy(enrolling = false) }
                        return
                    }
                    if (!MyDevices.join(eco)) {
                        show("The group it sent is not valid.")
                        _ui.update { it.copy(enrolling = false) }
                        return
                    }
                    enrollCode = null
                    refreshGroup()
                    show("This phone is now one of your devices.")
                    // The computer hangs up after letting us in: connect again, this time signing in as a member.
                    val host = savedHost
                    val port = savedPort
                    socket = null
                    webSocket.close(1000, null)
                    if (host != null) open(host, port)
                }
                "paired" -> {
                    prefs.edit().putString("computer", m.optString("computer")).apply()
                    savedHost?.let { keep(m.optString("computer").ifBlank { it }, it, savedPort, m.optString("token")) }
                    _ui.update { it.copy(phase = Phase.Connected, computer = m.optString("computer"), message = null) }
                    flushQueue()
                }
                "welcome" -> {
                    val sure = expected
                    if (sure == null || !java.security.MessageDigest.isEqual(sure.toByteArray(), m.optString("proof").toByteArray())) {
                        // Not the computer we paired with (or one that lost our pairing): keep the token, say so, hang up.
                        show("This computer could not prove it is the one you paired with.")
                        webSocket.close(1000, null)
                        return
                    }
                    prefs.edit().putString("computer", m.optString("computer")).apply()
                    savedHost?.let { keep(m.optString("computer").ifBlank { it }, it, savedPort, null) }   // its address may have changed
                    _ui.update { it.copy(phase = Phase.Connected, computer = m.optString("computer")) }
                    flushQueue()
                }
                "state" -> {
                    val state = try { parseState(m) } catch (_: Exception) { return }
                    _ui.update { it.copy(state = state, computer = state.computer.ifBlank { it.computer }) }
                    latest = state
                    prefs.edit().putString("lastState", text).apply()
                    changed()
                }
                "error" -> {
                    _ui.update { it.copy(enrolling = false) }
                    if (m.optString("code") == "notMember") {
                        // This computer is not in our group (or does not know us yet): its pairing token, if we have one, else a code.
                        val t = savedHost?.let { tokenFor(prefs.getString("computer", null), it) }
                        if (t != null) signInWithToken(webSocket, t) else _ui.update { it.copy(phase = Phase.NeedCode) }
                        return
                    }
                    if (m.optString("code") == "unpaired") {
                        savedHost?.let { dropToken(prefs.getString("computer", null), it) }
                        _ui.update { it.copy(phase = Phase.NeedCode, state = null) }
                    }
                    show(m.optString("error"))
                }
            }
        }

        /** Proves we hold the token without sending it, and asks the computer to prove it holds it too (a device pretending to be the computer learns nothing). */
        private fun signInWithToken(webSocket: WebSocket, token: String) {
            val mine = nonce()
            val theirs = challenge
            expected = proof(token, "computer:$mine:$theirs")
            webSocket.send(JSONObject().put("type", "hello").put("nonce", mine).put("proof", proof(token, "phone:$theirs:$mine")).toString())
        }

        private fun nonce(): String = android.util.Base64.encodeToString(ByteArray(32).also { java.security.SecureRandom().nextBytes(it) }, android.util.Base64.NO_WRAP)

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost(webSocket)

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = lost(webSocket)

        private fun lost(webSocket: WebSocket) {
            if (webSocket !== socket) return
            socket = null
            changed()
            if (!wanted) return
            _ui.update { it.copy(phase = Phase.Offline) }
            retry?.cancel()
            // Quick retries while something is on screen; slower ones for the notification (away from home, say).
            val wait = if ("app" in holders || "panel" in holders) 3000L else 20000L
            retry = scope.launch {
                delay(wait)
                val host = savedHost
                if (wanted && socket == null && host != null) open(host, savedPort)
            }
        }
    }

    /** HMAC-SHA256 of [text] with the token as key, base64 (same as PhoneStore.Proof on the computer). */
    private fun proof(token: String, text: String): String {
        val mac = javax.crypto.Mac.getInstance("HmacSHA256")
        mac.init(javax.crypto.spec.SecretKeySpec(token.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        return android.util.Base64.encodeToString(mac.doFinal(text.toByteArray(Charsets.UTF_8)), android.util.Base64.NO_WRAP)
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
