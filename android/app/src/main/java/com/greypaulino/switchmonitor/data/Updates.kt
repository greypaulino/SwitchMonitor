package com.greypaulino.switchmonitor.data

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * The phone's own "check for updates", like the one the computer has: it asks the project's GitHub releases for the newest version, and
 * when there is one it downloads the APK, proves it is the published one, and hands it to Android to install over this app. Android only
 * accepts an update signed with the same key as the installed app, so a file that is not the author's is refused by the system as well.
 *
 * Files come only from this project's releases (github.com/greypaulino/SwitchMonitor/releases/download/…). When the release has its
 * SHA256.json (and SHA256.json.sig, the author's signature of it, checked with the same public key as the computer's), the APK must match
 * the hash listed there.
 */
object Updates {
    const val REPOSITORY = "greypaulino/SwitchMonitor"
    private const val PUBLIC_KEY = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERa8OaKbbfmP8Zo+/sYK98Qp5mHiZj7Xb08ZNLlzn0/gS1fNyhEA4yYoFkHlCl1363/zvAMdtsvJG0O9jbJTgDw=="
    private const val JOB_ID = 4701
    private const val CHANNEL = "updates"
    private const val EVERY_HOURS = 24L
    private const val MIN_APK_BYTES = 1_000_000L

    enum class Step { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

    data class State(
        val step: Step = Step.Idle,
        val current: String = "",
        val latest: String? = null,
        val notes: String = "",
        val progress: Float = 0f,
        val error: String? = null,
        val lastCheck: Long = 0,
        val autoCheck: Boolean = true,
        /** Android has not been told yet that this app may install apps: the person has to allow it once. */
        val needsPermission: Boolean = false,
    )

    private val _state = MutableStateFlow(State())
    val state = _state.asStateFlow()

    private val _dialog = MutableStateFlow(false)
    /** The updates window is showing (opened from the menu, or by tapping the "update available" notification). */
    val dialog = _dialog.asStateFlow()

    fun openDialog() { _dialog.value = true }
    fun closeDialog() { _dialog.value = false }

    private data class Release(val version: String, val apkName: String, val apk: String, val manifest: String?, val signature: String?)

    private var release: Release? = null
    private val worker = Executors.newSingleThreadExecutor()
    private val http by lazy {
        OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build()
    }

    private fun prefs(context: Context) = context.getSharedPreferences("updates", Context.MODE_PRIVATE)

    fun currentVersion(context: Context): String = try {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0"
    } catch (_: Exception) { "0" }

    /** Reads the saved choices, and (re)arms the background check. Call when the app starts. */
    fun init(context: Context) {
        val p = prefs(context)
        _state.value = _state.value.copy(
            current = currentVersion(context), autoCheck = p.getBoolean("auto", true), lastCheck = p.getLong("last", 0),
            needsPermission = !canInstall(context),
        )
        schedule(context)
    }

    fun setAutoCheck(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean("auto", on).apply()
        _state.value = _state.value.copy(autoCheck = on)
        schedule(context)
    }

    /** When the app is opened: a quiet check if the last one was long ago (the answer shows only if there is something new). */
    fun checkIfDue(context: Context) {
        val s = _state.value
        if (s.autoCheck && System.currentTimeMillis() - s.lastCheck > 6 * 3_600_000L) check(context, manual = false)
    }

    // ---- Checking ----

    /** Asks GitHub for the newest release. <paramref name="done"/> is called on a worker thread when the answer is in. */
    fun check(context: Context, manual: Boolean, done: (() -> Unit)? = null) {
        val app = context.applicationContext
        val step = _state.value.step
        if (step == Step.Checking || step == Step.Downloading) { done?.invoke(); return }
        if (manual || step == Step.Idle || step == Step.UpToDate || step == Step.Failed) {
            _state.value = _state.value.copy(step = Step.Checking, error = null, current = currentVersion(app))
        }
        worker.execute {
            try {
                fetchLatest(app, manual)
            } catch (e: Exception) {
                if (manual) _state.value = _state.value.copy(step = Step.Failed, error = "Could not check for updates: ${e.message ?: e.javaClass.simpleName}")
                else _state.value = _state.value.copy(step = if (release != null) Step.Available else Step.Idle)
            } finally {
                done?.invoke()
            }
        }
    }

    private fun fetchLatest(context: Context, manual: Boolean) {
        val current = currentVersion(context)
        val request = Request.Builder().url("https://api.github.com/repos/$REPOSITORY/releases/latest")
            .header("Accept", "application/vnd.github+json").header("User-Agent", "SwitchMonitor-Android/$current").build()
        http.newCall(request).execute().use { response ->
            val now = System.currentTimeMillis()
            prefs(context).edit().putLong("last", now).apply()
            if (response.code == 404) { _state.value = _state.value.copy(step = Step.UpToDate, lastCheck = now); return }
            if (!response.isSuccessful) throw IllegalStateException("GitHub answered HTTP ${response.code}.")
            val root = JSONObject(response.body?.string() ?: throw IllegalStateException("Empty answer."))
            val tag = root.optString("tag_name")
            val latest = parse(tag) ?: throw IllegalStateException("The latest release has no version number.")
            if (compare(latest, parse(current) ?: listOf(0)) <= 0) {
                release = null
                _state.value = _state.value.copy(step = Step.UpToDate, latest = tag.trimStart('v'), lastCheck = now, notes = "")
                return
            }
            val assets = root.optJSONArray("assets") ?: JSONArray()
            var apk: Pair<String, String>? = null
            var manifest: String? = null
            var signature: String? = null
            val wanted = "SwitchMonitor-${tag.trimStart('v')}.apk"
            for (i in 0 until assets.length()) {
                val asset = assets.getJSONObject(i)
                val name = asset.optString("name")
                val url = asset.optString("browser_download_url")
                if (!allowed(url)) continue
                when {
                    name.equals(wanted, true) -> apk = name to url
                    name.endsWith(".apk", true) && apk == null -> apk = name to url
                    name.equals("SHA256.json", true) -> manifest = url
                    name.equals("SHA256.json.sig", true) -> signature = url
                }
            }
            if (apk == null) {
                // A release made for the computer only: nothing to install here.
                release = null
                _state.value = _state.value.copy(step = Step.UpToDate, latest = null, lastCheck = now)
                return
            }
            release = Release(tag.trimStart('v'), apk.first, apk.second, manifest, signature)
            _state.value = _state.value.copy(
                step = Step.Available, latest = tag.trimStart('v'), lastCheck = now, error = null,
                notes = root.optString("body").trim().take(600), needsPermission = !canInstall(context),
            )
            if (!manual) notifyAvailable(context, tag.trimStart('v'))
        }
    }

    private fun allowed(url: String) = url.startsWith("https://github.com/$REPOSITORY/releases/download/")

    private fun parse(text: String): List<Int>? {
        val match = Regex("^v?(\\d+(?:\\.\\d+){1,3})").find(text.trim()) ?: return null
        return match.groupValues[1].split('.').map { it.toInt() }
    }

    private fun compare(a: List<Int>, b: List<Int>): Int {
        for (i in 0 until maxOf(a.size, b.size)) {
            val c = a.getOrElse(i) { 0 }.compareTo(b.getOrElse(i) { 0 })
            if (c != 0) return c
        }
        return 0
    }

    // ---- Downloading and installing ----

    /** Downloads the update, proves it, and offers it to Android to install. */
    fun downloadAndInstall(context: Context) {
        val app = context.applicationContext
        val found = release ?: return
        if (_state.value.step == Step.Downloading) return
        _state.value = _state.value.copy(step = Step.Downloading, progress = 0f, error = null)
        worker.execute {
            try {
                val file = download(app, found)
                _state.value = _state.value.copy(step = Step.Ready, progress = 1f)
                install(app, file)
            } catch (e: Exception) {
                _state.value = _state.value.copy(step = Step.Failed, error = e.message ?: e.javaClass.simpleName)
            }
        }
    }

    /** The downloaded update is installed again (after the person allowed installs, for example). */
    fun installDownloaded(context: Context) {
        val app = context.applicationContext
        val found = release ?: return
        val file = File(File(app.cacheDir, "updates"), found.apkName)
        if (file.exists()) install(app, file) else downloadAndInstall(app)
    }

    private fun download(context: Context, found: Release): File {
        val folder = File(context.cacheDir, "updates").apply { mkdirs() }
        folder.listFiles()?.forEach { if (it.name != found.apkName) it.delete() }

        // The author's signed list of hashes, when the release has one.
        // Both are required (as on the computer): a release without them is not installed, even though Android also checks the app's key.
        if (found.manifest == null || found.signature == null)
            throw IllegalStateException("The release has no signed SHA256 list, so it is not installed.")
        val manifestBytes = bytes(found.manifest)
        if (!verify(manifestBytes, String(bytes(found.signature)).trim()))
            throw IllegalStateException("The release's SHA256 list is not signed by the author of SwitchMonitor.")
        val expected: String = hashOf(manifestBytes, found.apkName)
            ?: throw IllegalStateException("The release's SHA256 list does not include this file.")

        val target = File(folder, found.apkName)
        val part = File(folder, found.apkName + ".part")
        http.newCall(Request.Builder().url(found.apk).build()).execute().use { response ->
            if (!response.isSuccessful) throw IllegalStateException("GitHub answered HTTP ${response.code}.")
            val body = response.body ?: throw IllegalStateException("Empty download.")
            val total = body.contentLength()
            var done = 0L
            body.byteStream().use { input ->
                part.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buffer)
                        if (n < 0) break
                        output.write(buffer, 0, n)
                        done += n
                        if (total > 0) _state.value = _state.value.copy(progress = (done.toDouble() / total).toFloat().coerceIn(0f, 1f))
                    }
                }
            }
        }
        if (part.length() < MIN_APK_BYTES) { part.delete(); throw IllegalStateException("The downloaded file is too small to be the app.") }
        val actual = MessageDigest.getInstance("SHA-256").digest(part.readBytes()).joinToString("") { "%02X".format(it) }
        if (!actual.equals(expected, true)) { part.delete(); throw IllegalStateException("The downloaded file does not match the published checksum.") }
        checkPackage(context, part, found.version)
        if (target.exists()) target.delete()
        if (!part.renameTo(target)) throw IllegalStateException("The update could not be saved.")
        return target
    }

    private fun bytes(url: String): ByteArray {
        if (!allowed(url)) throw IllegalStateException("The release points outside this project's GitHub releases.")
        http.newCall(Request.Builder().url(url).build()).execute().use { r ->
            if (!r.isSuccessful) throw IllegalStateException("GitHub answered HTTP ${r.code}.")
            return r.body?.bytes() ?: ByteArray(0)
        }
    }

    private fun hashOf(manifest: ByteArray, file: String): String? {
        val text = String(manifest, Charsets.UTF_8).trimStart('﻿')
        val list = try { JSONArray(text) } catch (_: Exception) { return null }
        for (i in 0 until list.length()) {
            val entry = list.optJSONObject(i) ?: continue
            if (entry.optString("File").equals(file, true) && Regex("^[0-9A-Fa-f]{64}$").matches(entry.optString("Hash"))) return entry.optString("Hash")
        }
        return null
    }

    /** ECDSA P-256 over the manifest, the signature as raw r||s in base64 (what the computer's release tool writes). */
    private fun verify(manifest: ByteArray, signatureBase64: String): Boolean = try {
        val raw = android.util.Base64.decode(signatureBase64, android.util.Base64.DEFAULT)
        if (raw.size != 64) false else {
            val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(android.util.Base64.decode(PUBLIC_KEY, android.util.Base64.DEFAULT)))
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(key)
                update(manifest)
                verify(derOf(raw))
            }
        }
    } catch (_: Exception) { false }

    private fun derOf(raw: ByteArray): ByteArray {
        fun integer(part: ByteArray): ByteArray {
            var start = 0
            while (start < part.size - 1 && part[start] == 0.toByte()) start++
            var value = part.copyOfRange(start, part.size)
            if (value[0] < 0) value = byteArrayOf(0) + value
            return byteArrayOf(0x02, value.size.toByte()) + value
        }
        val r = integer(raw.copyOfRange(0, 32))
        val s = integer(raw.copyOfRange(32, 64))
        return byteArrayOf(0x30, (r.size + s.size).toByte()) + r + s
    }

    /** What was downloaded is this app, newer than the installed one, and signed by the same key (else Android would refuse it anyway). */
    @Suppress("DEPRECATION")
    private fun checkPackage(context: Context, file: File, version: String) {
        val pm = context.packageManager
        val flags = if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
        val archive = pm.getPackageArchiveInfo(file.path, flags) ?: run { file.delete(); throw IllegalStateException("The downloaded file is not an app package.") }
        if (archive.packageName != context.packageName) { file.delete(); throw IllegalStateException("The downloaded file is another app.") }
        val mine = pm.getPackageInfo(context.packageName, flags)
        val newer = if (Build.VERSION.SDK_INT >= 28) archive.longVersionCode > mine.longVersionCode else archive.versionCode > mine.versionCode
        if (!newer) { file.delete(); throw IllegalStateException("The downloaded file is not newer than the installed app.") }
        if (Build.VERSION.SDK_INT >= 28) {
            val a = archive.signingInfo?.apkContentsSigners?.map { it.toByteArray().contentHashCode() }?.toSet()
            val b = mine.signingInfo?.apkContentsSigners?.map { it.toByteArray().contentHashCode() }?.toSet()
            if (a != null && b != null && a != b) {
                file.delete()
                throw IllegalStateException("This update is signed with a different key than the installed app (a test build, perhaps): Android would refuse it. Uninstall the app once and install the published one.")
            }
        }
        if (archive.versionName != null && parse(archive.versionName!!) != parse(version)) { file.delete(); throw IllegalStateException("The file's version does not match the release.") }
    }

    fun canInstall(context: Context): Boolean =
        Build.VERSION.SDK_INT < 26 || context.packageManager.canRequestPackageInstalls()

    /** Opens Android's own screen where the person lets this app install apps (once). */
    fun allowInstalls(context: Context) {
        if (Build.VERSION.SDK_INT >= 26) context.startActivity(
            Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    }

    private fun install(context: Context, file: File) {
        if (!canInstall(context)) {
            _state.value = _state.value.copy(step = Step.Ready, needsPermission = true)
            allowInstalls(context)
            return
        }
        _state.value = _state.value.copy(needsPermission = false)
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
        context.startActivity(
            Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    }

    // ---- Every day, in the background ----

    private fun schedule(context: Context) {
        val scheduler = context.getSystemService(Context.JOB_SCHEDULER_SERVICE) as? JobScheduler ?: return
        if (!_state.value.autoCheck) { scheduler.cancel(JOB_ID); return }
        if (scheduler.getPendingJob(JOB_ID) != null) return
        scheduler.schedule(
            JobInfo.Builder(JOB_ID, ComponentName(context, UpdateJob::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPeriodic(TimeUnit.HOURS.toMillis(EVERY_HOURS))
                .setPersisted(true)
                .build(),
        )
    }

    private fun notifyAvailable(context: Context, version: String) {
        val p = prefs(context)
        if (p.getString("notified", "") == version) return   // once per version
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(NotificationChannel(CHANNEL, "Updates", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(
            context, 0,
            Intent(context, com.greypaulino.switchmonitor.MainActivity::class.java).putExtra("openUpdates", true).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(context, CHANNEL) else @Suppress("DEPRECATION") Notification.Builder(context)
        manager.notify(
            47821,
            builder.setSmallIcon(android.R.drawable.stat_sys_download_done).setContentTitle("SwitchMonitor $version is available")
                .setContentText("Tap to read about it and update.").setContentIntent(open).setAutoCancel(true).build(),
        )
        p.edit().putString("notified", version).apply()
    }
}

/** Runs once a day (Android decides the moment) while Check for updates automatically is on: a quiet check that notifies when there is a new version. */
class UpdateJob : JobService() {
    override fun onStartJob(params: JobParameters): Boolean {
        Updates.init(applicationContext)
        if (!Updates.state.value.autoCheck) return false
        Updates.check(applicationContext, manual = false) { jobFinished(params, false) }
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean = true
}
