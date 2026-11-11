package com.greypaulino.switchmonitor.data

import android.content.ClipData
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Base64
import androidx.core.content.FileProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Images and files between this phone and the computers of the group. Everything travels as numbered pieces ("blob" messages of at
 * most 512 KB): an image is for the clipboard (when Shared clipboard is on); a file is saved in Downloads/SwitchMonitor.
 */
object Transfers {
    private const val PIECE = 512 * 1024
    private const val MAX_IMAGE = 100L * 1024 * 1024
    private const val MAX_FILE = 10L * 1024 * 1024 * 1024
    private const val MAX_TRANSFERS = 32

    private class Incoming(val purpose: String, val name: String) {
        var memory: ByteArrayOutputStream? = null
        var out: OutputStream? = null
        var pending: Uri? = null
        var file: File? = null
        var received = 0L
        var last = System.currentTimeMillis()
    }

    private val incoming = ConcurrentHashMap<String, Incoming>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** The image this phone put on its own clipboard (so it is not sent back). */
    @Volatile var lastImageUri: String = ""

    // ---- Receiving ----

    fun onBlob(context: Context, m: JSONObject) {
        val id = m.optString("id")
        if (id.isEmpty() || id.length > 64) return
        val purpose = m.optString("purpose")
        val index = m.optInt("index")
        val total = m.optInt("total")
        val size = m.optLong("size")
        val bytes = try { Base64.decode(m.optString("data"), Base64.DEFAULT) } catch (_: Exception) { return }
        purge()
        try {
            if (!incoming.containsKey(id) && incoming.size >= MAX_TRANSFERS) return
            val t = incoming.getOrPut(id) { Incoming(purpose, m.optString("name")) }
            t.last = System.currentTimeMillis()
            when (purpose) {
                "image" -> {
                    if (!Remote.ui.value.clipboard || size > MAX_IMAGE) { drop(id); return }
                    val memory = t.memory ?: ByteArrayOutputStream().also { t.memory = it }
                    memory.write(bytes)
                    if (memory.size() > MAX_IMAGE) { drop(id); return }
                    if (index >= total - 1) {
                        val data = memory.toByteArray()
                        drop(id)
                        putOnClipboard(context, data)
                    }
                }
                "file" -> {
                    if (index == 0) {
                        if (size > MAX_FILE) { drop(id); return }
                        open(context, t, cleanName(t.name))
                    }
                    val out = t.out ?: return
                    t.received += bytes.size
                    if (t.received > MAX_FILE) { drop(id); return }
                    out.write(bytes)
                    if (index >= total - 1) {
                        finish(context, t)
                        incoming.remove(id)
                        Remote.notice("Received ${cleanName(t.name)}: it is in Downloads/SwitchMonitor.")
                        notifyReceived(context, cleanName(t.name))
                    }
                }
                else -> drop(id)
            }
        } catch (_: Exception) {
            drop(id)
        }
    }

    /** A real notification (the app may be in the background): "X sent you a file". */
    private fun notifyReceived(context: Context, name: String) {
        try {
            val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
            if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(android.app.NotificationChannel("files", "Received files", android.app.NotificationManager.IMPORTANCE_DEFAULT))
            val open = android.app.PendingIntent.getActivity(context, 0, android.content.Intent(android.app.DownloadManager.ACTION_VIEW_DOWNLOADS), android.app.PendingIntent.FLAG_IMMUTABLE)
            val n = androidx.core.app.NotificationCompat.Builder(context, "files").setSmallIcon(com.greypaulino.switchmonitor.R.drawable.g_monitor)
                .setContentTitle("File received").setContentText("$name is in Downloads/SwitchMonitor").setContentIntent(open).setAutoCancel(true).build()
            manager.notify(name.hashCode(), n)
        } catch (_: Exception) { }
    }

    private fun purge() {
        val now = System.currentTimeMillis()
        for ((id, t) in incoming) if (now - t.last > 5 * 60_000) drop(id)
    }

    private fun drop(id: String) {
        val t = incoming.remove(id) ?: return
        try { t.out?.close() } catch (_: Exception) { }
        t.file?.delete()
    }

    private fun cleanName(name: String): String {
        val n = name.substringAfterLast('/').substringAfterLast('\\').replace(Regex("[<>:\"|?*\\u0000-\\u001f]"), "_").trim().trimEnd('.')
        return if (n.isEmpty()) "file" else n.take(120)
    }

    private fun open(context: Context, t: Incoming, name: String) {
        if (Build.VERSION.SDK_INT >= 29) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/SwitchMonitor")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw java.io.IOException("no room")
            t.pending = uri
            t.out = context.contentResolver.openOutputStream(uri)
        } else {
            val dir = File(context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "SwitchMonitor").apply { mkdirs() }
            var target = File(dir, name)
            var n = 2
            while (target.exists()) target = File(dir, "${name.substringBeforeLast('.')} ($n).${name.substringAfterLast('.', "")}".trimEnd('.')).also { n++ }
            t.file = target
            t.out = target.outputStream()
        }
    }

    private fun finish(context: Context, t: Incoming) {
        t.out?.close()
        t.out = null
        t.file = null
        t.pending?.let { context.contentResolver.update(it, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null) }
    }

    /** The image goes onto this phone's clipboard, as a content address the other apps can read. */
    private fun putOnClipboard(context: Context, data: ByteArray) {
        val dir = File(context.cacheDir, "shared").apply { mkdirs() }
        dir.listFiles()?.forEach { if (System.currentTimeMillis() - it.lastModified() > 60 * 60_000) it.delete() }
        val file = File(dir, "clip-${System.currentTimeMillis()}.img")
        file.writeBytes(data)
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
        lastImageUri = uri.toString()
        Handler(Looper.getMainLooper()).post {
            val manager = context.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
            val mime = if (data.size > 3 && data[0] == 0xFF.toByte()) "image/jpeg" else "image/png"
            manager.setPrimaryClip(ClipData(android.content.ClipDescription("SwitchMonitor", arrayOf(mime)), ClipData.Item(uri)))
        }
    }

    // ---- Sending ----

    /** An image was copied on this phone: it goes to every computer of the group (when Shared clipboard is on). */
    fun sendImage(context: Context, uri: Uri) {
        if (!Remote.ui.value.clipboard || uri.toString() == lastImageUri) return
        lastImageUri = uri.toString()
        scope.launch {
            try {
                val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return@launch
                if (bytes.size > MAX_IMAGE) return@launch
                sendStream(bytes.inputStream(), bytes.size.toLong(), "image", "clipboard", all = true)
            } catch (_: Exception) { }
        }
    }

    /** Files chosen on this phone go to the computer it is controlling. */
    fun sendFiles(context: Context, uris: List<Uri>, device: String = "") {
        scope.launch {
            for (uri in uris) {
                var name = "file"
                var size = -1L
                context.contentResolver.query(uri, null, null, null, null)?.use { c ->
                    if (c.moveToFirst()) {
                        val n = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        val s = c.getColumnIndex(OpenableColumns.SIZE)
                        if (n >= 0) name = c.getString(n) ?: name
                        if (s >= 0 && !c.isNull(s)) size = c.getLong(s)
                    }
                }
                if (size > MAX_FILE) { Remote.notice("$name is bigger than 10 GB."); continue }
                try {
                    // "*" is every computer of the group, one after the other; "" the one this phone is aiming at.
                    val ids = if (device == "*") Remote.ui.value.group.map { it.deviceId } else listOf(device)
                    Remote.notice("Sending $name…")
                    for (id in ids) {
                        val stream = context.contentResolver.openInputStream(uri) ?: continue
                        stream.use {
                            if (size >= 0) sendStream(it, size, "file", name, all = false, device = id)
                            else { val whole = it.readBytes(); sendStream(whole.inputStream(), whole.size.toLong(), "file", name, all = false, device = id) }
                        }
                    }
                    Remote.notice("Sent $name.")
                } catch (_: Exception) {
                    Remote.notice("$name could not be sent.")
                    return@launch
                }
            }
        }
    }

    private suspend fun sendStream(stream: java.io.InputStream, length: Long, purpose: String, name: String, all: Boolean, device: String = "") {
        val id = UUID.randomUUID().toString().replace("-", "")
        val total = if (length <= 0) 1 else ((length + PIECE - 1) / PIECE).toInt()   // the length is known: a file's, or an image's bytes
        val buffer = ByteArray(PIECE)
        var index = 0
        while (true) {
            var filled = 0
            while (filled < buffer.size) {
                val n = stream.read(buffer, filled, buffer.size - filled)
                if (n < 0) break
                filled += n
            }
            val last = index >= total - 1
            val message = JSONObject().put("type", "blob").put("id", id).put("purpose", purpose).put("name", name).put("size", length)
                .put("index", index).put("total", total)
                .put("data", Base64.encodeToString(buffer, 0, filled, Base64.NO_WRAP)).put("from", MyDevices.deviceId)
                .put("fromName", Build.MODEL ?: "Phone")
            // Do not run ahead of the network: wait while a lot is still queued.
            while (Remote.queuedBytes(device) > 3_000_000) delay(25)
            if (all) Remote.sendToAll(message) else if (!Remote.sendToTarget(message, device)) { Remote.notice("The computer is not reachable."); return }
            index++
            if (last) break
        }
    }
}
