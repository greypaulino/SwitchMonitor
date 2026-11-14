package com.greypaulino.switchmonitor.data

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** A device of "My devices": its public key, signed by a computer that was already in the group (the first one signs itself). */
data class Member(val deviceId: String, val name: String, val kind: String, val key: String, val issuer: String, val at: Long, val sig: String) {
    val isComputer get() = kind == "computer"

    /** The text the issuer signed (the same on the computer: Ecosystem.cs, MemberCert.Payload). */
    fun payload(ecosystem: String) = "member|$ecosystem|$deviceId|$name|$kind|$key|$issuer|$at"

    fun toJson(): JSONObject = JSONObject().put("deviceId", deviceId).put("name", name).put("kind", kind).put("key", key)
        .put("issuer", issuer).put("at", at).put("sig", sig)

    companion object {
        fun from(o: JSONObject?): Member? {
            o ?: return null
            val id = o.optString("deviceId")
            val kind = o.optString("kind")
            if (id.length !in 8..64 || (kind != "computer" && kind != "phone") || !o.has("at")) return null
            return Member(id, o.optString("name"), kind, o.optString("key"), o.optString("issuer"), o.optLong("at"), o.optString("sig"))
        }
    }
}

/** A computer of the group took a device out: certificates issued before [at] stop counting. */
data class Revoked(val deviceId: String, val issuer: String, val at: Long, val sig: String) {
    fun payload(ecosystem: String) = "revoke|$ecosystem|$deviceId|$issuer|$at"

    fun toJson(): JSONObject = JSONObject().put("deviceId", deviceId).put("issuer", issuer).put("at", at).put("sig", sig)

    companion object {
        fun from(o: JSONObject?): Revoked? {
            o ?: return null
            if (o.optString("deviceId").isEmpty() || !o.has("at")) return null
            return Revoked(o.optString("deviceId"), o.optString("issuer"), o.optLong("at"), o.optString("sig"))
        }
    }
}

/**
 * This phone in "My devices": the group of computers and phones of one person (see Ecosystem.cs on the computer). The phone has
 * its own key pair in the Android Keystore (the private half never leaves it), joins by typing the code a computer of the group
 * shows, and from then on signs in to any computer of the group with that key: no pairing per computer. What it knows of the
 * group is checked certificate by certificate, back to the computer that founded it, and kept sealed with the Keystore.
 */
internal object MyDevices {
    private const val ALIAS = "switchmonitor-device-signing"
    private const val STORE = "AndroidKeyStore"

    private lateinit var prefs: SharedPreferences
    private var id: String? = null
    private val certs = LinkedHashMap<String, Member>()
    private val revoked = HashMap<String, Revoked>()

    lateinit var deviceId: String
        private set

    fun init(context: Context) {
        if (::prefs.isInitialized) return
        prefs = context.applicationContext.getSharedPreferences("switchmonitor", Context.MODE_PRIVATE)
        deviceId = prefs.getString("deviceId", null) ?: ByteArray(16).also { SecureRandom().nextBytes(it) }
            .joinToString("") { "%02x".format(it) }.also { prefs.edit().putString("deviceId", it).apply() }
        load()
    }

    @Synchronized fun inGroup() = id != null

    /** The computers of the group other than this one (their address is found by search). */
    @Synchronized fun computers(): List<Member> = certs.values.filter { it.isComputer && active(it) }.sortedBy { it.name.lowercase() }

    @Synchronized fun isMember(device: String) = certs[device]?.let { active(it) } == true

    // ---- This phone's key ----

    private fun privateKey(): PrivateKey {
        val store = KeyStore.getInstance(STORE).apply { load(null) }
        (store.getKey(ALIAS, null) as? PrivateKey)?.let { return it }
        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, STORE)
        generator.initialize(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .build(),
        )
        generator.generateKeyPair()
        return store.getKey(ALIAS, null) as PrivateKey
    }

    /** This phone's public key (base64 SubjectPublicKeyInfo, what the computer imports). */
    val publicKey: String
        get() {
            privateKey()
            val store = KeyStore.getInstance(STORE).apply { load(null) }
            return Base64.encodeToString(store.getCertificate(ALIAS).publicKey.encoded, Base64.NO_WRAP)
        }

    /** The signature (base64, 64 bytes: r then s, as the computer's .NET reads it) of a text. */
    fun sign(text: String): String {
        val signature = Signature.getInstance("SHA256withECDSA").apply { initSign(privateKey()); update(text.toByteArray(Charsets.UTF_8)) }
        return Base64.encodeToString(derToRaw(signature.sign()), Base64.NO_WRAP)
    }

    /** Whether [signature] is the signature of [text] by the member [device]. */
    @Synchronized fun verify(device: String, text: String, signature: String): Boolean {
        val member = certs[device]?.takeIf { active(it) } ?: return false
        return verifyWith(member.key, text, signature)
    }

    private fun verifyWith(publicKey: String, text: String, signature: String): Boolean = try {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.decode(publicKey, Base64.NO_WRAP)))
        Signature.getInstance("SHA256withECDSA").run {
            initVerify(key)
            update(text.toByteArray(Charsets.UTF_8))
            verify(rawToDer(Base64.decode(signature, Base64.NO_WRAP)))
        }
    } catch (_: Exception) { false }

    // ---- Joining, with the code a computer of the group shows ----

    /** What this phone sends instead of the code itself. */
    fun enrollProof(code: String, nonce: String, binding: String = ""): String = hmac(code, "enroll:$nonce:$deviceId:$publicKey" + (if (binding.isNotEmpty()) ":$binding" else ""))

    /** What the computer must answer, to show it knew the code. */
    fun enrollAnswer(code: String, nonce: String, approver: String, ecosystem: String, binding: String = ""): String = hmac(code, "approver:$nonce:$deviceId:$approver:$ecosystem" + (if (binding.isNotEmpty()) ":$binding" else ""))

    private fun hmac(key: String, text: String): String {
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key.toByteArray(Charsets.UTF_8), "HmacSHA256")) }
        return Base64.encodeToString(mac.doFinal(text.toByteArray(Charsets.UTF_8)), Base64.NO_WRAP)
    }

    /** Takes in the group the approving computer sent ({id, members, revoked}); true when this phone's own certificate is in it. */
    @Synchronized fun join(payload: JSONObject): Boolean {
        val newId = payload.optString("id")
        if (id != null || newId.length != 32) return false
        id = newId
        certs.clear(); revoked.clear()
        mergeLocked(payload.optJSONArray("members"), payload.optJSONArray("revoked"))
        val mine = certs[deviceId]
        if (mine == null || !active(mine) || mine.key != publicKey) {
            id = null; certs.clear(); revoked.clear()
            return false
        }
        save()
        return true
    }

    @Synchronized fun leave() {
        id = null
        certs.clear(); revoked.clear()
        save()
    }

    /**
     * What this phone signs with its own key to tell the others it is leaving: send it to them, then call [leave]. Each device that
     * learns it takes this phone out of its list. Null when the phone is in no group.
     */
    @Synchronized fun createLeave(): Revoked? {
        val eco = id ?: return null
        val own = certs[deviceId]?.at ?: 0L
        val unsigned = Revoked(deviceId, deviceId, maxOf(System.currentTimeMillis(), own + 1), "")
        return unsigned.copy(sig = sign(unsigned.payload(eco)))
    }

    /** Takes in the members and revocations another member sent (each one checked). The phone leaves by itself when it was taken out. */
    @Synchronized fun merge(members: JSONArray?, revocations: JSONArray?) {
        if (id == null) return
        if (mergeLocked(members, revocations)) save()
        val mine = certs[deviceId]
        if (mine != null && !active(mine)) { id = null; certs.clear(); revoked.clear(); save() }
    }

    /** What to send a computer: every certificate and revocation known. */
    @Synchronized fun export(): JSONObject = JSONObject()
        .put("members", JSONArray().also { a -> certs.values.forEach { a.put(it.toJson()) } })
        .put("revoked", JSONArray().also { a -> revoked.values.forEach { a.put(it.toJson()) } })

    private fun active(c: Member) = revoked[c.deviceId]?.let { c.at > it.at } ?: true

    private fun mergeLocked(members: JSONArray?, revocations: JSONArray?): Boolean {
        val pendingCerts = (0 until (members?.length() ?: 0)).mapNotNull { Member.from(members?.optJSONObject(it)) }.toMutableList()
        val pendingRevs = (0 until (revocations?.length() ?: 0)).mapNotNull { Revoked.from(revocations?.optJSONObject(it)) }.toMutableList()
        var any = false
        var progress = true
        while (progress) {
            progress = false
            for (c in pendingCerts.toList()) {
                if (!certChecks(c)) continue
                pendingCerts.remove(c)
                if ((certs[c.deviceId]?.at ?: Long.MIN_VALUE) >= c.at) continue
                certs[c.deviceId] = c
                any = true; progress = true
            }
            for (r in pendingRevs.toList()) {
                if (!revocationChecks(r)) continue
                pendingRevs.remove(r)
                if ((revoked[r.deviceId]?.at ?: Long.MIN_VALUE) >= r.at) continue
                revoked[r.deviceId] = r
                any = true; progress = true
            }
        }
        return any
    }

    private fun certChecks(c: Member): Boolean {
        val eco = id ?: return false
        try { KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.decode(c.key, Base64.NO_WRAP))) } catch (_: Exception) { return false }
        if (c.issuer == c.deviceId) {
            // The first computer vouches for itself: its key must be the one the group's id is made of.
            if (!c.isComputer) return false
            val hash = MessageDigest.getInstance("SHA-256").digest(Base64.decode(c.key, Base64.NO_WRAP))
            if (hash.joinToString("") { "%02x".format(it) }.substring(0, 32) != eco) return false
            return verifyWith(c.key, c.payload(eco), c.sig)
        }
        val issuer = certs[c.issuer] ?: return false
        if (!issuer.isComputer) return false
        revoked[issuer.deviceId]?.let { if (c.at > it.at) return false }   // a removed computer cannot vouch any more
        return verifyWith(issuer.key, c.payload(eco), c.sig)
    }

    private fun revocationChecks(r: Revoked): Boolean {
        val eco = id ?: return false
        val issuer = certs[r.issuer] ?: return false
        // A computer of the group takes a device out, or a device leaves by itself (signed with its own key).
        return (issuer.isComputer || r.issuer == r.deviceId) && verifyWith(issuer.key, r.payload(eco), r.sig)
    }

    // ---- Saved, sealed with the Keystore ----

    private fun load() {
        val text = prefs.getString("groupSealed", null)?.let { Vault.open(it) } ?: return
        try {
            val o = JSONObject(text)
            val saved = o.optString("id")
            if (saved.length != 32) return
            id = saved
            mergeLocked(o.optJSONArray("members"), o.optJSONArray("revoked"))
            val mine = certs[deviceId]
            if (mine == null || mine.key != publicKey) { id = null; certs.clear(); revoked.clear() }
        } catch (_: Exception) { id = null; certs.clear(); revoked.clear() }
    }

    private fun save() {
        val json = export().put("id", id ?: "").toString()
        if (id == null) { prefs.edit().remove("groupSealed").apply(); return }
        Vault.seal(json)?.let { prefs.edit().putString("groupSealed", it).apply() }
    }

    // ---- ECDSA signatures: Java makes DER, the computer's .NET reads and writes r||s (IEEE P1363) ----

    private fun fixed32(n: ByteArray): ByteArray {
        val b = BigInteger(1, n).toByteArray()
        val t = if (b.size > 32) b.copyOfRange(b.size - 32, b.size) else b
        return ByteArray(32 - t.size) + t
    }

    private fun derToRaw(der: ByteArray): ByteArray {
        var i = 2
        if (der[1].toInt() and 0x80 != 0) i += der[1].toInt() and 0x7F
        val rl = der[i + 1].toInt() and 0xFF
        val r = der.copyOfRange(i + 2, i + 2 + rl)
        i += 2 + rl
        val sl = der[i + 1].toInt() and 0xFF
        val s = der.copyOfRange(i + 2, i + 2 + sl)
        return fixed32(r) + fixed32(s)
    }

    private fun rawToDer(raw: ByteArray): ByteArray {
        require(raw.size == 64)
        val r = BigInteger(1, raw.copyOfRange(0, 32)).toByteArray()
        val s = BigInteger(1, raw.copyOfRange(32, 64)).toByteArray()
        val body = byteArrayOf(0x02, r.size.toByte()) + r + byteArrayOf(0x02, s.size.toByte()) + s
        return byteArrayOf(0x30, body.size.toByte()) + body
    }
}
