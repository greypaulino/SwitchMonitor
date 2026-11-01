package com.greypaulino.switchmonitor.data

import android.content.Context
import android.net.wifi.WifiManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

/**
 * Finds SwitchMonitor computers on this Wi-Fi: a UDP broadcast "SWITCHMONITOR?" to port 47821, answered by each
 * computer with {"app":"SwitchMonitor","computer":"...","port":47820}.
 */
object Discovery {
    const val DISCOVERY_PORT = 47821
    private const val QUESTION = "SWITCHMONITOR?"

    suspend fun search(context: Context, timeoutMs: Long = 2500): List<Found> = withContext(Dispatchers.IO) {
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val lock = wifi?.createMulticastLock("switchmonitor-search")?.apply { setReferenceCounted(false) }
        val found = LinkedHashMap<String, Found>()
        try {
            lock?.acquire()
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = 300
                val question = QUESTION.toByteArray()
                val targets = broadcastAddresses()
                val deadline = System.currentTimeMillis() + timeoutMs
                var nextAsk = 0L
                val buffer = ByteArray(1024)
                while (System.currentTimeMillis() < deadline) {
                    if (System.currentTimeMillis() >= nextAsk) {   // ask a few times: a single UDP packet can be lost
                        for (target in targets) {
                            try { socket.send(DatagramPacket(question, question.size, target, DISCOVERY_PORT)) } catch (_: Exception) { }
                        }
                        nextAsk = System.currentTimeMillis() + 800
                    }
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(packet)
                    } catch (_: SocketTimeoutException) {
                        continue
                    }
                    val host = packet.address?.hostAddress ?: continue
                    try {
                        val answer = JSONObject(String(packet.data, 0, packet.length, Charsets.UTF_8))
                        if (answer.optString("app") != "SwitchMonitor") continue
                        found[host] = Found(answer.optString("computer", host), host, answer.optInt("port", Remote.DEFAULT_PORT))
                    } catch (_: Exception) { }
                }
            }
        } catch (_: Exception) {
        } finally {
            try { lock?.release() } catch (_: Exception) { }
        }
        found.values.sortedBy { it.name.lowercase() }
    }

    /** 255.255.255.255 plus each Wi-Fi/Ethernet subnet's own broadcast address (some routers drop the first). */
    private fun broadcastAddresses(): List<InetAddress> {
        val list = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        try {
            for (nic in NetworkInterface.getNetworkInterfaces()) {
                if (!nic.isUp || nic.isLoopback) continue
                for (address in nic.interfaceAddresses) {
                    if (address.address is Inet4Address) address.broadcast?.let { if (it !in list) list += it }
                }
            }
        } catch (_: Exception) { }
        return list
    }
}
