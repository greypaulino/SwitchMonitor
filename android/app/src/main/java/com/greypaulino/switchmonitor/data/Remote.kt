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
import okio.ByteString
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
    /** The 4-digit number of this connection: the computer shows the same one when it asks to allow this phone. */
    val verification: String = "",
    /** The device id of the computer this phone is connected through (a member of the group). */
    val gateway: String = "",
    /** The computer of the group the mouse and keyboard act on ("" = the one connected). */
    val target: String = "",
    /** What each computer (by device id) offers to open or close, as last asked. */
    val apps: Map<String, AppsList> = emptyMap(),
    /** The favorite programs of each computer (kept on this phone). */
    val favorites: Map<String, List<AppItem>> = emptyMap(),
    /** The mouse: pointer and scroll speed (1 = as is). */
    val pointerSpeed: Float = 1f,
    val scrollSpeed: Float = 1f,
    /** Brightness moves together across the whole group (the chain / link button with several computers). */
    val chain: Boolean = false,
    /** What is copied on this phone is shared with the group, and theirs here (needs the switch on the computers too). */
    val clipboard: Boolean = false,
    /** The icons of the favorite programs, by "device id|program". */
    val icons: Map<String, android.graphics.Bitmap> = emptyMap(),
    /** The computers of the group that answer right now (device id → it is a laptop). The mouse and Remote tabs list only these. */
    val available: Map<String, Boolean> = emptyMap(),
    /** What the program that plays media is doing on each computer (device id): silenced, audible, playing. */
    val media: Map<String, MediaState> = emptyMap(),
    /** The person chose "Use another computer": a computer outside the group, by pairing code. Otherwise the app belongs to the group. */
    val manual: Boolean = false,
) {
    /** The app works for the whole group: it connects by itself to the computers of My devices and has no chosen computer. */
    val groupMode: Boolean get() = inGroup && !manual

    /** The computers of the group that answer right now. */
    fun liveGroup(): List<Found> = group.filter { it.deviceId in available }
}

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

    // The computer has proved who it is (welcome / memberWelcome checked, or the pairing this phone started finished): only then are
    // its states, clipboard, files and program lists believed. Before that, a device pretending to be it could push anything.
    @Volatile private var verified = false
    @Volatile private var pairingSent = false
    @Volatile private var socket: SecureSocket? = null
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
        val saved = loadFavorites()
        _ui.update {
            it.copy(favorites = saved, pointerSpeed = prefs.getFloat("pointerSpeed", 1f), scrollSpeed = prefs.getFloat("scrollSpeed", 1f),
                chain = prefs.getBoolean("chain", false), clipboard = prefs.getBoolean("clipboardShare", false), icons = loadIcons(saved))
        }
        val host = prefs.getString("host", null)
        // A known computer starts as Connecting, so the first frame is already the control screen (not the search).
        _ui.update { it.copy(phase = if (host != null) Phase.Connecting else it.phase, host = host, computer = prefs.getString("computer", null), paired = pairedList(), inGroup = MyDevices.inGroup(), group = groupList(), manual = prefs.getBoolean("manual", false)) }
        latest = prefs.getString("lastState", null)?.let { try { parseState(JSONObject(it)).let { s -> s.copy(displays = s.displays.filter { d -> !d.remote }) } } catch (_: Exception) { null } }
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
        if (_ui.value.clipboard) SyncService.update(appContext, true)
    }

    /** The app went to the background. */
    fun stop() = release("app")

    @Synchronized
    fun hold(who: String) {
        holders += who
        wanted = true
        val host = savedHost
        if (host != null && socket == null) open(host, savedPort)
        startGroupLoop()
    }

    @Synchronized
    fun release(who: String) {
        holders -= who
        if (holders.isNotEmpty()) return
        wanted = false
        retry?.cancel()
        groupJob?.cancel(); groupJob = null
        closeLinks()
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
            .put("key", MyDevices.publicKey).put("proof", MyDevices.enrollProof(clean, challenge, s.binding)).toString())
    }

    // ---- The group: the phone talks to EVERY computer of "My devices", not to one that passes the others on. Each gives its own
    // displays; the screen shows them together and a command goes to the computer that owns the display. ----

    private class Link(val deviceId: String, val name: String, val host: String, val port: Int) {
        @Volatile var socket: SecureSocket? = null
        @Volatile var state: PcState? = null
        @Volatile var connected = false
        @Volatile private var active = true
        private var challenge = ""
        private var mine: String? = null
        private var retry: Job? = null

        fun open() {
            if (!active) return
            val secure = SecureSocket()
            socket = secure
            connected = false
            secure.raw = client.newWebSocket(Request.Builder().url("http://$host:$port/ws").build(), object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) = receive(secure, text, encrypted = false)

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    if (secure !== socket) return
                    val plain = secure.decrypt(bytes.toByteArray())
                    if (plain == null) { secure.close(1000, null); return }   // not the next frame, or changed: the link is closed
                    receive(secure, plain, encrypted = true)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost(secure)
                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = lost(secure)
            })
        }

        private fun receive(secure: SecureSocket, text: String, encrypted: Boolean) {
            if (secure !== socket) return
            if (secure.keyed && !encrypted) return   // after the key exchange only encrypted frames count
            val m = try { JSONObject(text) } catch (_: Exception) { return }
            val type = m.optString("type")
            if (!secure.keyed && type != "challenge") return
            when (type) {
                "challenge" -> {
                    if (secure.keyed) return
                    if (secure.start(m) != null) { secure.close(1000, null); return }
                    challenge = m.optString("nonce")
                    val (nonce, hello) = memberHello(challenge, secure.binding)
                    mine = nonce
                    secure.send(hello)
                }
                "memberWelcome" -> {
                    MyDevices.merge(m.optJSONArray("members"), m.optJSONArray("revoked"))
                    val theirs = m.optString("deviceId")
                    if (theirs != deviceId || mine == null || !MyDevices.verify(theirs, "server:$mine:$challenge:$theirs:${secure.binding}", m.optString("signature"))) {
                        secure.close(1000, null)
                        return
                    }
                    connected = true
                    rebuild()
                }
                "state" -> {
                    if (!connected) return
                    state = try { parseState(m) } catch (_: Exception) { return }
                    rebuild()
                }
                "apps" -> if (connected) appsArrived(deviceId, m)
                "clip" -> if (connected) clipboardArrived(m.optString("text"))
                "blob" -> if (connected) Transfers.onBlob(appContext, m)
            }
        }

        private fun lost(secure: SecureSocket) {
            if (secure !== socket) return
            socket = null
            connected = false
            state = null
            rebuild()
            if (!active || !wanted) return
            retry = scope.launch { delay(4000); if (active && wanted && socket == null) open() }
        }

        fun send(text: String): Boolean = connected && socket?.send(text) == true

        fun close() {
            active = false
            retry?.cancel()
            socket?.close(1000, null)
            socket = null
            connected = false
            state = null
        }
    }

    private val links = ConcurrentHashMap<String, Link>()
    private var groupJob: Job? = null

    /** Opens what a member sends to sign in: its signature of the challenge and what it knows of the group. Returns (our nonce, the message). */
    private fun memberHello(challenge: String, binding: String): Pair<String, String> {
        val mine = android.util.Base64.encodeToString(ByteArray(32).also { java.security.SecureRandom().nextBytes(it) }, android.util.Base64.NO_WRAP)
        val known = MyDevices.export()
        val hello = JSONObject().put("type", "member").put("deviceId", MyDevices.deviceId).put("nonce", mine)
            .put("signature", MyDevices.sign("member:$challenge:$mine:${MyDevices.deviceId}:$binding"))
            .put("members", known.getJSONArray("members")).put("revoked", known.getJSONArray("revoked")).toString()
        return mine to hello
    }

    /** While something needs the connection: look for the computers of the group every half minute and keep one link to each. */
    private fun startGroupLoop() {
        if (groupJob?.isActive == true || !MyDevices.inGroup()) return
        groupJob = scope.launch {
            while (wanted) {
                val now = _ui.value
                if (now.groupMode && socket == null) search()   // nobody connected: look for the group's computers (and connect to the first)
                else if (now.groupMode && now.gateway.isNotEmpty() &&
                    (MyDevices.computers().any { it.deviceId != now.gateway && links[it.deviceId]?.connected != true })) search()
                else syncLinks()
                delay(if (now.groupMode && socket == null) 6_000 else 30_000)
            }
        }
    }

    /** In group mode with no computer connected: connects to the first computer of the group the search found. */
    private fun autoConnect() {
        val now = _ui.value
        if (!wanted || !now.groupMode || socket != null) return
        val members = MyDevices.computers()
        val here = now.found.firstOrNull { f -> members.any { it.deviceId == f.deviceId } } ?: return
        choose(here.host, here.port, here.name, viaGroup = true)
    }

    /** Back from "Use another computer" to the group: the isolated connection is dropped and the group's computers are looked for. */
    fun backToGroup() {
        prefs.edit().putBoolean("manual", false).remove("host").remove("computer").remove("lastState").apply()
        synchronized(this) {
            retry?.cancel()
            socket?.close(1000, null)
            socket = null
            holders += "app"
            wanted = true
        }
        primaryState = null
        selectedId = null
        closeLinks()
        latest = null
        changed()
        _ui.update { it.copy(manual = false, host = null, computer = null, state = null, gateway = "", available = emptyMap(), phase = Phase.Offline, target = "") }
        startGroupLoop()
        search()
    }

    /** One link per computer of the group (other than the one the phone connected through), at the address the search found. */
    private fun syncLinks() {
        if (!_ui.value.groupMode || !wanted) { closeLinks(); return }
        val gateway = _ui.value.gateway
        val members = MyDevices.computers().filter { it.deviceId != gateway && it.deviceId != MyDevices.deviceId }
        for ((id, link) in links.entries.toList()) if (members.none { it.deviceId == id }) { link.close(); links.remove(id) }
        for (m in members) {
            val found = _ui.value.found.firstOrNull { it.deviceId == m.deviceId } ?: continue
            val old = links[m.deviceId]
            if (old != null && old.host == found.host && old.port == found.port) continue
            old?.close()
            links[m.deviceId] = Link(m.deviceId, m.name, found.host, found.port).also { it.open() }
        }
        rebuild()
    }

    private fun closeLinks() {
        for (link in links.values) link.close()
        links.clear()
        rebuild()
    }

    // The state the screens show: every computer's own displays together. What the phone selected is the phone's own choice.
    @Volatile private var primaryState: PcState? = null
    @Volatile private var selectedId: String? = null
    @Volatile private var severalSources = false
    private val owners = ConcurrentHashMap<String, Pair<String, String>>()   // display id (as shown) -> owner, id for the owner
    private var selectSentAt = 0L

    @Synchronized private fun rebuild() {
        val gateway = _ui.value.gateway
        val sources = mutableListOf<Pair<String, PcState>>()
        primaryState?.let { sources += gateway to it }
        for ((id, link) in links) link.state?.let { sources += id to it }
        val availableNow = sources.associate { (id, s) -> id to s.portable }
        val mediaNow = sources.mapNotNull { (id, s) -> s.media?.let { id to it } }.toMap()
        if (_ui.value.available != availableNow || _ui.value.media != mediaNow) _ui.update {
            // A computer that went away is no longer the one the mouse and Remote act on.
            it.copy(available = availableNow, media = mediaNow, target = if (it.target.isEmpty() || it.target in availableNow) it.target else "")
        }
        if (sources.isEmpty()) {
            if (_ui.value.state != null) { _ui.update { it.copy(state = null) }; latest = latest }
            return
        }
        val several = sources.size > 1
        val shown = mutableListOf<Display>()
        owners.clear()
        for ((owner, state) in sources) for (d in state.displays.filter { !it.remote }) {
            val id = if (several) "$owner|${d.id}" else d.id
            owners[id] = owner to d.id
            shown += d.copy(id = id, owner = owner, key = d.id)
        }
        val chosen = selectedId?.takeIf { s -> shown.any { it.id == s } }
            ?: shown.firstOrNull { d -> sources.firstOrNull { it.first == d.owner }?.second?.displays?.any { it.id == d.key && it.selected } == true }?.id
            ?: shown.firstOrNull()?.id
        selectedId = chosen
        val displays = shown.map { it.copy(selected = it.id == chosen) }
        val selected = displays.firstOrNull { it.selected }
        val ownerState = sources.firstOrNull { it.first == selected?.owner }?.second ?: sources.first().second
        val base = primaryState ?: sources.first().second
        // The picture modes and the volume belong to the computer of the selected display (and only while it also has it selected).
        val ownerFollows = selected == null || ownerState.displays.firstOrNull { it.selected }?.id == selected.key
        severalSources = several
        val merged = base.copy(
            displays = displays,
            modes = ownerState.modes.takeIf { ownerFollows },
            volume = ownerState.volume,
            volumeFor = ownerState.volumeFor,
            // With several computers the chain button is the phone's: every display of the group moves together.
            canLink = if (several) displays.count { it.on } > 1 else base.canLink,
            linked = if (several) _ui.value.chain else base.linked,
            linkOwner = "",
        )
        latest = merged
        _ui.update { it.copy(state = merged, computer = merged.computer.ifBlank { it.computer }) }
        changed()
        // The owner of the selected display must have it selected too, so its modes and volume follow.
        if (selected != null && !ownerFollows && System.currentTimeMillis() - selectSentAt > 1500) {
            selectSentAt = System.currentTimeMillis()
            sendTo(selected.owner, JSONObject().put("type", "select").put("display", selected.key))
        }
    }

    /** The message to the computer with this device id (the one connected through, or a link); false when it is not reachable. */
    private fun sendTo(owner: String, message: JSONObject): Boolean {
        val text = message.toString()
        if (owner.isEmpty() || owner == _ui.value.gateway) { send(message); return true }
        return links[owner]?.send(text) == true
    }

    /** A command about one display goes to its computer, with the id that computer knows. */
    private fun sendDisplay(display: String, message: JSONObject) {
        val (owner, key) = owners[display] ?: ("" to display)
        sendTo(owner, message.put("display", key))
    }

    private fun selectedOwner(): String = owners[selectedId ?: ""]?.first ?: ""

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
        val shown = message.optString("display")
        if (shown.isNotEmpty()) {
            val (owner, key) = owners[shown] ?: ("" to shown)
            message.put("display", key)
            if (owner.isNotEmpty() && owner != _ui.value.gateway && links[owner]?.send(message.toString()) == true) return
        }
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
            autoConnect()
            syncLinks()
        }
    }

    /** Connect to a computer from the search, or typed by hand ("10.0.0.45" or "10.0.0.45:47820"). [viaGroup]: the app did it for the group, not the person. */
    fun choose(host: String, port: Int = DEFAULT_PORT, name: String? = null, viaGroup: Boolean = false) {
        val clean = host.trim().removePrefix("http://").removeSuffix("/")
        val (h, p) = clean.split(':').let { if (it.size == 2) it[0] to (it[1].toIntOrNull() ?: port) else clean to port }
        if (h.isEmpty()) return
        val sameComputer = h == savedHost
        val known = name ?: pairedList().firstOrNull { it.host == h }?.name
        val edit = prefs.edit().putString("host", h).putInt("port", p)
        if (known != null) edit.putString("computer", known) else if (!sameComputer) edit.remove("computer")
        edit.putBoolean("manual", !viaGroup && MyDevices.inGroup()).apply()
        wanted = true
        socket?.close(1000, null)
        socket = null
        primaryState = null
        _ui.update { it.copy(host = h, computer = known ?: it.computer.takeIf { sameComputer }, state = null, manual = !viaGroup && MyDevices.inGroup()) }
        rebuild()
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
        val inGroup = MyDevices.inGroup()
        prefs.edit().remove("host").remove("computer").remove("lastState").putBoolean("manual", inGroup).apply()
        primaryState = null
        selectedId = null
        latest = null
        changed()
        _ui.update {
            it.copy(phase = Phase.Choose, host = null, computer = null, state = null, gateway = "", target = "", available = emptyMap(), verification = "",
                paired = pairedList(), inGroup = inGroup, group = groupList(), manual = inGroup, enrolling = false)
        }
        closeLinks()
        holders += "app"
        wanted = true
        search()
    }

    fun pair(code: String) {
        val name = deviceName()
        pairingSent = true
        _ui.update { it.copy(enrolling = true) }   // the computer asks its owner before it answers
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
                    if (severalSources && _ui.value.chain) {
                        // Chain: every display of the group goes to the same level.
                        for (d in latest?.displays.orEmpty().filter { it.on && it.ready }) sendDisplay(d.id, JSONObject().put("type", "brightness").put("value", v))
                    } else sendDisplay(key, JSONObject().put("type", "brightness").put("value", v))
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
                sendTo(selectedOwner(), JSONObject().put("type", "volume").put("value", v))
                delay(70)
            }
        }
    }

    fun mute(on: Boolean) {
        latest?.let { st -> st.volume?.let { latest = st.copy(volume = it.copy(muted = on)); changed() } }
        sendTo(selectedOwner(), JSONObject().put("type", "mute").put("on", on))
    }

    fun select(display: String) {
        selectedId = display
        selectSentAt = System.currentTimeMillis()   // sent right below: rebuild() must not send it again
        rebuild()
        sendDisplay(display, JSONObject().put("type", "select"))
    }
    fun link(on: Boolean) {
        if (severalSources) {
            // The chain across the group is the phone's own: the displays of every computer follow one slider.
            prefs.edit().putBoolean("chain", on).apply()
            _ui.update { it.copy(chain = on) }
            rebuild()
        } else send(JSONObject().put("type", "link").put("on", on))
    }
    fun profile(name: String) = send(JSONObject().put("type", "profile").put("name", name))
    fun mode(value: Int) { sendTo(selectedOwner(), JSONObject().put("type", "mode").put("value", value)) }
    fun input(display: String, code: Int) = sendDisplay(display, JSONObject().put("type", "input").put("code", code))
    fun cycle(display: String, code: Int, on: Boolean) =
        sendDisplay(display, JSONObject().put("type", "cycle").put("code", code).put("on", on))
    // ---- The mouse: how fast the pointer and the scroll go ----

    val pointerSpeed: Float get() = _ui.value.pointerSpeed
    val scrollSpeed: Float get() = _ui.value.scrollSpeed

    fun setPointerSpeed(value: Float) {
        val v = value.coerceIn(0.4f, 3f)
        prefs.edit().putFloat("pointerSpeed", v).apply()
        _ui.update { it.copy(pointerSpeed = v) }
    }

    fun setScrollSpeed(value: Float) {
        val v = value.coerceIn(0.4f, 3f)
        prefs.edit().putFloat("scrollSpeed", v).apply()
        _ui.update { it.copy(scrollSpeed = v) }
    }

    // ---- The Remote tab: media keys, programs and power of one computer of the group (the one chosen, or the one connected) ----

    /** The device id of the computer the Remote tab and the mouse act on. */
    fun aimed(): String = _ui.value.target.ifEmpty { _ui.value.gateway }

    fun media(key: String) { sendTo(aimed(), JSONObject().put("type", "media").put("key", key)) }

    /** Asks the computer for its programs (and the icons of the favorites, which the phone keeps). */
    fun requestApps() {
        val owner = aimed()
        val ids = org.json.JSONArray().also { a -> _ui.value.favorites[owner].orEmpty().forEach { a.put(it.id) } }
        sendTo(owner, JSONObject().put("type", "apps").put("icons", ids))
    }
    /** The icons of some programs (the list being shown in the picker) that are not favorites yet. */
    fun requestIcons(ids: List<String>) {
        if (ids.isEmpty()) return
        sendTo(aimed(), JSONObject().put("type", "apps").put("icons", org.json.JSONArray().also { a -> ids.forEach { a.put(it) } }))
    }
    fun launch(id: String) { sendTo(aimed(), JSONObject().put("type", "launch").put("id", id)) }
    fun closeApp(id: String) { sendTo(aimed(), JSONObject().put("type", "closeapp").put("id", id)) }
    fun power(action: String) { sendTo(aimed(), JSONObject().put("type", "power").put("action", action)) }

    private fun loadFavorites(): Map<String, List<AppItem>> = try {
        val o = JSONObject(prefs.getString("favorites", "{}") ?: "{}")
        o.keys().asSequence().associateWith { id ->
            val a = o.getJSONArray(id)
            (0 until a.length()).map { a.getJSONObject(it).let { f -> AppItem(f.optString("id"), f.optString("name")) } }
        }
    } catch (_: Exception) { emptyMap() }

    private fun saveFavorites(map: Map<String, List<AppItem>>) {
        val o = JSONObject()
        for ((id, list) in map) o.put(id, org.json.JSONArray().also { a -> list.forEach { a.put(JSONObject().put("id", it.id).put("name", it.name)) } })
        prefs.edit().putString("favorites", o.toString()).apply()
        _ui.update { it.copy(favorites = map) }
    }

    fun addFavorite(app: AppItem) {
        val owner = aimed()
        val map = _ui.value.favorites.toMutableMap()
        val list = map[owner].orEmpty()
        if (list.none { it.id == app.id }) map[owner] = list + AppItem(app.id, app.name)
        saveFavorites(map)
        requestApps()   // brings its icon
    }

    fun removeFavorite(id: String) {
        val owner = aimed()
        val map = _ui.value.favorites.toMutableMap()
        map[owner] = map[owner].orEmpty().filter { it.id != id }
        saveFavorites(map)
    }

    private fun parseApps(m: JSONObject): AppsList {
        val a = m.optJSONArray("apps")
        val items = (0 until (a?.length() ?: 0)).mapNotNull { a?.optJSONObject(it) }
            .map { AppItem(it.optString("id"), it.optString("name"), it.optBoolean("running")) }
        return AppsList(m.optBoolean("allowed"), items)
    }

    private fun iconFile(owner: String, id: String) =
        java.io.File(java.io.File(appContext.filesDir, "appicons").also { it.mkdirs() }, "${owner}_$id".replace(Regex("[^A-Za-z0-9._-]"), "_") + ".png")

    private fun loadIcons(favorites: Map<String, List<AppItem>>): Map<String, android.graphics.Bitmap> {
        val map = HashMap<String, android.graphics.Bitmap>()
        for ((owner, list) in favorites) for (app in list) {
            val file = iconFile(owner, app.id)
            if (file.exists()) android.graphics.BitmapFactory.decodeFile(file.path)?.let { map["$owner|${app.id}"] = it }
        }
        return map
    }

    private fun appsArrived(owner: String, m: JSONObject) {
        val icons = HashMap<String, android.graphics.Bitmap>()
        m.optJSONObject("icons")?.let { o ->
            for (id in o.keys()) try {
                val bytes = android.util.Base64.decode(o.getString(id), android.util.Base64.DEFAULT)
                iconFile(owner, id).writeBytes(bytes)
                android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.let { icons["$owner|$id"] = it }
            } catch (_: Exception) { }
        }
        _ui.update { it.copy(apps = it.apps + (owner to parseApps(m)), icons = it.icons + icons) }
    }

    // ---- The shared clipboard: text copied on this phone goes to every computer of the group, and theirs lands here ----

    @Volatile private var lastClip = ""

    fun setClipboardShare(on: Boolean) {
        prefs.edit().putBoolean("clipboardShare", on).apply()
        _ui.update { it.copy(clipboard = on) }
        SyncService.update(appContext, on)
    }

    /** Something was copied on this phone (the app's clipboard listener): it goes out when sharing is on. */
    fun clipboardChanged(text: String) {
        if (!_ui.value.clipboard || text.isEmpty() || text == lastClip || text.length > 100_000) return
        val message = JSONObject().put("type", "clip").put("id", java.util.UUID.randomUUID().toString().replace("-", ""))
            .put("text", text).put("from", MyDevices.deviceId).toString()
        val s = socket
        var sent = false
        if (s != null && _ui.value.phase == Phase.Connected) sent = s.send(message)
        for (link in links.values) if (link.send(message)) sent = true
        if (sent) lastClip = text   // not connected yet: it stays pending and goes out the next time the app is in front
    }

    /** "Send my clipboard now" (the menu): sends what is on the clipboard even if it was copied before sharing was turned on. */
    fun sendClipboardNow(text: String) {
        if (!_ui.value.clipboard) { show("Turn on Shared clipboard first."); return }
        lastClip = ""
        clipboardChanged(text)
        show("Clipboard sent.")
    }

    // ---- What Transfers needs: a short message on screen, and a way to reach the computers ----

    fun notice(text: String) = show(text)

    /** The message to the computer this phone aims at; false when it is not reachable. */
    fun sendToTarget(message: JSONObject, device: String = ""): Boolean {
        val ui = _ui.value
        return sendTo(device.ifEmpty { ui.target.ifEmpty { ui.gateway } }, message)
    }

    /** The message to every computer of the group this phone is connected to. */
    fun sendToAll(message: JSONObject) {
        val text = message.toString()
        val s = socket
        if (s != null && _ui.value.phase == Phase.Connected) s.send(text)
        for (link in links.values) link.send(text)
    }

    /** Bytes still waiting to go out to the computer this phone aims at. */
    fun queuedBytes(device: String = ""): Long {
        val ui = _ui.value
        val target = device.ifEmpty { ui.target.ifEmpty { ui.gateway } }
        return (if (target.isEmpty() || target == ui.gateway) socket?.queueSize() else links[target]?.socket?.queueSize()) ?: 0L
    }

    private fun clipboardArrived(text: String) {
        if (!_ui.value.clipboard || text.isEmpty() || text == lastClip) return
        lastClip = text
        android.os.Handler(android.os.Looper.getMainLooper()).post {
            val manager = appContext.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
            manager.setPrimaryClip(android.content.ClipData.newPlainText("SwitchMonitor", text))
        }
    }

    // ---- The phone as the computer's mouse and keyboard (dropped, never queued, when not connected) ----

    private fun sendInput(op: JSONObject) {
        val s = socket
        val ui = _ui.value
        if (ui.target.isNotEmpty() && ui.target != ui.gateway) {
            // Another computer of the group: straight to it when linked, else the one connected passes it on.
            val link = links[ui.target]
            if (link?.connected == true) { link.send(op.put("type", "input").toString()); return }
            op.put("target", ui.target)
        }
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
    fun hwheel(delta: Int) = sendInput(JSONObject().put("op", "wheel").put("d", delta).put("h", true))
    fun key(code: Int, up: Boolean) = sendInput(JSONObject().put("op", "key").put("vk", code).put("up", up))
    fun text(value: String) = sendInput(JSONObject().put("op", "text").put("s", value))

    fun main(display: String) = sendDisplay(display, JSONObject().put("type", "main"))
    fun preferred(display: String) = sendDisplay(display, JSONObject().put("type", "preferred"))

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
        val secure = SecureSocket()
        socket = secure
        secure.raw = client.newWebSocket(request, Listener(secure))
    }

    private class Listener(private val secure: SecureSocket) : WebSocketListener() {
        // The computer's answer to our own challenge, expected in "welcome" (see "challenge" below).
        @Volatile private var expected: String? = null

        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (secure !== socket) return
            verified = false
            // The computer speaks first with a challenge; without a token for it, the code is needed.
            if (!MyDevices.inGroup() && savedHost?.let { tokenFor(prefs.getString("computer", null), it) } == null) _ui.update { it.copy(phase = Phase.NeedCode) }
        }

        override fun onMessage(webSocket: WebSocket, text: String) = receive(text, encrypted = false)

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            if (secure !== socket) return
            val plain = secure.decrypt(bytes.toByteArray())
            if (plain == null) { show("The connection to the computer was not safe and was closed."); secure.close(1000, null); return }   // not the next frame, or changed
            receive(plain, encrypted = true)
        }

        private fun receive(text: String, encrypted: Boolean) {
            if (secure !== socket) return
            val webSocket: WebSocket = secure   // every send below is encrypted
            if (secure.keyed && !encrypted) return   // after the key exchange only encrypted frames count
            val m = try { JSONObject(text) } catch (_: Exception) { return }
            if (!secure.keyed && m.optString("type") != "challenge") return   // before the keys only the challenge is believed
            when (m.optString("type")) {
                "challenge" -> {
                    if (secure.keyed) return
                    val problem = secure.start(m)
                    if (problem != null) { show(problem); secure.close(1000, null); return }
                    challenge = m.optString("nonce")
                    _ui.update { it.copy(verification = secure.verification) }
                    if (MyDevices.inGroup()) {
                        // A member of the group signs the challenge with its key: no token, no code. It tells what it knows of the
                        // group, and checks the computer's answer the same way.
                        val mine = nonce()
                        memberNonce = mine
                        val known = MyDevices.export()
                        webSocket.send(JSONObject().put("type", "member").put("deviceId", MyDevices.deviceId).put("nonce", mine)
                            .put("signature", MyDevices.sign("member:$challenge:$mine:${MyDevices.deviceId}:${secure.binding}"))
                            .put("members", known.getJSONArray("members")).put("revoked", known.getJSONArray("revoked")).toString())
                        return
                    }
                    val t = savedHost?.let { tokenFor(prefs.getString("computer", null), it) } ?: return
                    signInWithToken(webSocket, t)
                }
                "apps" -> if (verified) appsArrived(_ui.value.gateway, m)
                "clip" -> if (verified) clipboardArrived(m.optString("text"))
                "blob" -> if (verified) Transfers.onBlob(appContext, m)
                "memberWelcome" -> {
                    MyDevices.merge(m.optJSONArray("members"), m.optJSONArray("revoked"))
                    val theirs = m.optString("deviceId")
                    val mine = memberNonce
                    if (mine == null || !MyDevices.verify(theirs, "server:$mine:$challenge:$theirs:${secure.binding}", m.optString("signature"))) {
                        show("This computer could not prove it is in your group.")
                        webSocket.close(1000, null)
                        return
                    }
                    verified = true
                    val name = m.optString("computer")
                    prefs.edit().putString("computer", name).apply()
                    _ui.update { it.copy(phase = Phase.Connected, computer = name, group = groupList(), gateway = theirs) }
                    flushQueue()
                    links.remove(theirs)?.close()   // the computer connected through is not also a link
                    startGroupLoop()
                    scope.launch { if (_ui.value.found.isEmpty()) search() else syncLinks() }
                }
                "enrolled" -> {
                    val code = enrollCode
                    val eco = m.optJSONObject("ecosystem")
                    if (code == null || eco == null) return
                    val answer = MyDevices.enrollAnswer(code, challenge, m.optString("deviceId"), eco.optString("id"), secure.binding)
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
                    prefs.edit().putBoolean("manual", false).apply()
                    refreshGroup()
                    _ui.update { it.copy(manual = false) }
                    show("This phone is now one of your devices.")
                    // The computer hangs up after letting us in: connect again, this time signing in as a member.
                    val host = savedHost
                    val port = savedPort
                    socket = null
                    webSocket.close(1000, null)
                    if (host != null) open(host, port)
                }
                "paired" -> {
                    // Only the answer to the pairing this phone asked for: a stray "paired" would replace the saved token.
                    if (!pairingSent) return
                    pairingSent = false
                    verified = true
                    prefs.edit().putString("computer", m.optString("computer")).apply()
                    savedHost?.let { keep(m.optString("computer").ifBlank { it }, it, savedPort, m.optString("token")) }
                    _ui.update { it.copy(phase = Phase.Connected, computer = m.optString("computer"), message = null, enrolling = false) }
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
                    verified = true
                    prefs.edit().putString("computer", m.optString("computer")).apply()
                    savedHost?.let { keep(m.optString("computer").ifBlank { it }, it, savedPort, null) }   // its address may have changed
                    _ui.update { it.copy(phase = Phase.Connected, computer = m.optString("computer")) }
                    flushQueue()
                }
                "state" -> {
                    if (!verified) return
                    val state = try { parseState(m) } catch (_: Exception) { return }
                    primaryState = state
                    prefs.edit().putString("lastState", text).apply()
                    rebuild()
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
            val tail = ":" + secure.binding
            expected = proof(token, "computer:$mine:$theirs$tail")
            webSocket.send(JSONObject().put("type", "hello").put("nonce", mine).put("proof", proof(token, "phone:$theirs:$mine$tail")).toString())
        }

        private fun nonce(): String = android.util.Base64.encodeToString(ByteArray(32).also { java.security.SecureRandom().nextBytes(it) }, android.util.Base64.NO_WRAP)

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost()

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = lost()

        private fun lost() {
            if (secure !== socket) return
            socket = null
            primaryState = null
            changed()
            if (!wanted) return
            links.values.firstOrNull { it.connected }?.let { other ->
                // Another computer of the group is live: connect through it (the lost one becomes a link when it is back).
                scope.launch { choose(other.host, other.port, other.name, viaGroup = true) }
                return
            }
            rebuild()
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
