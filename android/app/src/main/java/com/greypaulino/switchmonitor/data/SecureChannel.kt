package com.greypaulino.switchmonitor.data

import okhttp3.Request
import okhttp3.WebSocket
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The encrypted channel to a computer (the same as SecureChannel.cs): the challenge carries the computer's ECDH P-256 key, this
 * phone answers with its own (the only plain message it sends) and both derive two AES-256-GCM keys, one per direction. Every
 * message after that is a binary frame: counter (8 bytes) + ciphertext + tag (16), accepted only with the next counter.
 * [binding] (a hash of both keys) goes into every proof of identity, so a device in the middle cannot pass a proof on.
 */
internal class SecureSession(
    private val sendKey: ByteArray, private val sendPrefix: ByteArray,
    private val receiveKey: ByteArray, private val receivePrefix: ByteArray,
    val binding: String,
) {
    private var sendCounter = 0L
    private var receiveCounter = 0L
    private val lock = Any()

    private fun nonce(prefix: ByteArray, counter: Long): ByteArray {
        val n = ByteArray(12)
        System.arraycopy(prefix, 0, n, 0, 4)
        for (i in 0 until 8) n[4 + i] = (counter ushr (56 - 8 * i)).toByte()
        return n
    }

    fun encrypt(text: String): ByteArray = synchronized(lock) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(sendKey, "AES"), GCMParameterSpec(128, nonce(sendPrefix, sendCounter)))
        val body = cipher.doFinal(text.toByteArray(Charsets.UTF_8))   // ciphertext followed by the 16-byte tag
        val frame = ByteArray(8 + body.size)
        for (i in 0 until 8) frame[i] = (sendCounter ushr (56 - 8 * i)).toByte()
        System.arraycopy(body, 0, frame, 8, body.size)
        sendCounter++
        frame
    }

    /** The message in a frame, or null when it is not the next frame of this connection or was changed. */
    fun decrypt(frame: ByteArray): String? = synchronized(lock) {
        if (frame.size < 8 + 16) return null
        var counter = 0L
        for (i in 0 until 8) counter = (counter shl 8) or (frame[i].toLong() and 0xFF)
        if (counter != receiveCounter) return null
        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(receiveKey, "AES"), GCMParameterSpec(128, nonce(receivePrefix, counter)))
            val plain = cipher.doFinal(frame, 8, frame.size - 8)
            receiveCounter++
            String(plain, Charsets.UTF_8)
        } catch (_: Exception) { null }
    }
}

/** The WebSocket to a computer: it sends nothing but encrypted frames once [start] has run (and nothing at all before). */
internal class SecureSocket : WebSocket {
    @Volatile var raw: WebSocket? = null
    @Volatile private var session: SecureSession? = null

    val keyed get() = session != null
    val binding get() = session?.binding ?: ""

    /** A 4-digit number both ends of this connection can compute: the computer shows it when it asks to allow this phone. A device in the middle makes them differ. */
    val verification: String
        get() {
            val b = binding
            if (b.isEmpty()) return ""
            val hash = MessageDigest.getInstance("SHA-256").digest(("SwitchMonitor verification:$b").toByteArray(Charsets.UTF_8))
            var n = 0L
            for (i in 0 until 4) n = (n shl 8) or (hash[i].toLong() and 0xFF)
            return "%04d".format(n % 10000)
        }

    /**
     * The computer's challenge was read: sends this phone's key (plain, the only plain message) and derives the session.
     * An error text when the challenge has no key (an old version, or somebody removing it): never falls back to plain.
     */
    fun start(challenge: JSONObject): String? {
        val serverKey = challenge.optString("ecdh")
        val nonce = challenge.optString("nonce")
        if (serverKey.isEmpty() || nonce.isEmpty()) return "This computer does not encrypt: update SwitchMonitor on it."
        return try {
            val generator = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
            val mine = generator.generateKeyPair()
            val myKey = android.util.Base64.encodeToString(mine.public.encoded, android.util.Base64.NO_WRAP)
            val serverBytes = android.util.Base64.decode(serverKey, android.util.Base64.DEFAULT)
            val clientBytes = mine.public.encoded
            if (serverBytes.size !in 60..100) return "The computer sent a key that cannot be used."
            val theirs = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(serverBytes))
            val secret = KeyAgreement.getInstance("ECDH").apply { init(mine.private); doPhase(theirs, true) }.generateSecret()
            val transcript = MessageDigest.getInstance("SHA-256").digest(
                "SwitchMonitor/channel/3".toByteArray(Charsets.US_ASCII) + serverBytes + clientBytes + nonce.toByteArray(Charsets.UTF_8),
            )
            val material = hkdf(secret, transcript, "SwitchMonitor keys".toByteArray(Charsets.US_ASCII), 72)
            session = SecureSession(
                sendKey = material.copyOfRange(0, 32), sendPrefix = material.copyOfRange(64, 68),
                receiveKey = material.copyOfRange(32, 64), receivePrefix = material.copyOfRange(68, 72),
                binding = android.util.Base64.encodeToString(transcript, android.util.Base64.NO_WRAP),
            )
            val hello = JSONObject().put("type", "key").put("ecdh", myKey).toString()
            if (raw?.send(hello) != true) { session = null; return "The connection closed." }
            null
        } catch (e: Exception) {
            "The computer sent a key that cannot be used."
        }
    }

    fun decrypt(bytes: ByteArray): String? = session?.decrypt(bytes)

    // The sends of the rest of the app go through here: encrypted, in order, or not at all.
    private val lock = Any()
    override fun send(text: String): Boolean = synchronized(lock) {
        val s = session ?: return false
        raw?.send(s.encrypt(text).toByteString()) ?: false
    }
    override fun send(bytes: ByteString): Boolean = false
    override fun queueSize(): Long = raw?.queueSize() ?: 0L
    override fun close(code: Int, reason: String?): Boolean = raw?.close(code, reason) ?: false
    override fun cancel() { raw?.cancel() }
    override fun request(): Request = raw!!.request()

    private fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        fun hmac(key: ByteArray, data: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)
        val prk = hmac(salt, ikm)
        val out = ByteArrayOutputStream()
        var t = ByteArray(0)
        var i = 1
        while (out.size() < length) {
            t = hmac(prk, t + info + byteArrayOf(i.toByte()))
            out.write(t)
            i++
        }
        return out.toByteArray().copyOf(length)
    }
}
