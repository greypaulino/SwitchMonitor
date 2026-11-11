using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Remote;

/// <summary>
/// The encrypted channel every connection uses after its first message. The server's challenge carries a fresh ECDH P-256 public key;
/// the other side answers with its own ("key"); both derive the same two AES-256-GCM keys (one per direction) and from then on every
/// message travels as a binary WebSocket frame: counter (8 bytes) + ciphertext + tag (16). A frame is only accepted with the next
/// counter, so nothing can be replayed, reordered or changed. Nobody watching the network sees a token, a clipboard or a file.
/// <see cref="SecureSession.Binding"/> (a hash of both keys) goes into every proof of identity (token, member signature, code), so a
/// device sitting in the middle cannot pass a proof made for one connection on to another.
/// </summary>
public sealed class SecureSession
{
    private readonly AesGcm sendKey, receiveKey;
    private readonly byte[] sendPrefix, receivePrefix;
    private ulong sendCounter, receiveCounter;
    private readonly object gate = new();

    internal SecureSession(byte[] sendKey, byte[] sendPrefix, byte[] receiveKey, byte[] receivePrefix, string binding)
    {
        this.sendKey = new AesGcm(sendKey, 16);
        this.receiveKey = new AesGcm(receiveKey, 16);
        this.sendPrefix = sendPrefix;
        this.receivePrefix = receivePrefix;
        Binding = binding;
    }

    /// <summary>A hash of both public keys of this connection (base64): part of every proof of identity.</summary>
    public string Binding { get; }

    /// <summary>The frame for a message: counter, ciphertext and tag. Call in the order the frames are sent.</summary>
    public byte[] Encrypt(string json)
    {
        byte[] plain = Encoding.UTF8.GetBytes(json);
        lock (gate)
        {
            var frame = new byte[8 + plain.Length + 16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(frame, sendCounter);
            sendKey.Encrypt(Nonce(sendPrefix, sendCounter), plain, frame.AsSpan(8, plain.Length), frame.AsSpan(8 + plain.Length, 16));
            sendCounter++;
            return frame;
        }
    }

    /// <summary>The message in a frame, or null when it is not the next frame of this connection or was changed.</summary>
    public string? Decrypt(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8 + 16) return null;
        lock (gate)
        {
            ulong counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(frame);
            if (counter != receiveCounter) return null;
            int length = frame.Length - 8 - 16;
            var plain = new byte[length];
            try { receiveKey.Decrypt(Nonce(receivePrefix, counter), frame.Slice(8, length), frame.Slice(8 + length, 16), plain); }
            catch (CryptographicException) { return null; }
            receiveCounter++;
            return Encoding.UTF8.GetString(plain);
        }
    }

    private static byte[] Nonce(byte[] prefix, ulong counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }
}

public static class SecureChannel
{
    public const int Version = 3;
    private static readonly ConditionalWeakTable<WebSocket, SecureSession> sessions = new();

    /// <summary>The session of a connection, or null before its keys were exchanged.</summary>
    public static SecureSession? Of(WebSocket socket) => sessions.TryGetValue(socket, out var session) ? session : null;

    public static string Binding(WebSocket socket) => Of(socket)?.Binding ?? "";

    private static void Attach(WebSocket socket, SecureSession session) => sessions.AddOrUpdate(socket, session);

    // ---- The key exchange ----

    /// <summary>A new key for a connection (the server's goes in its challenge as <c>ecdh</c>).</summary>
    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string PublicKeyOf(ECDiffieHellman key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// The server got the client's "key": derives the session and attaches it to the connection. False when the key is not valid.
    /// </summary>
    public static bool ServerAccept(WebSocket socket, ECDiffieHellman mine, string serverNonce, string clientPublicKey)
    {
        var session = Derive(mine, serverNonce, PublicKeyOf(mine), clientPublicKey, isServer: true);
        if (session is null) return false;
        Attach(socket, session);
        return true;
    }

    /// <summary>
    /// The client read the server's challenge: sends its own key (the one plain message it ever sends) and derives the session.
    /// Throws when the challenge has no key (an old version, or somebody removing it): never falls back to plain text.
    /// </summary>
    public static async Task StartAsync(WebSocket socket, JsonObject challenge, CancellationToken cancel = default)
    {
        string serverKey = (string?)challenge["ecdh"] ?? "";
        string serverNonce = (string?)challenge["nonce"] ?? "";
        if (serverKey.Length == 0 || serverNonce.Length == 0)
            throw new InvalidOperationException("The other computer does not encrypt: update SwitchMonitor on it.");
        using var mine = NewKey();
        string myKey = PublicKeyOf(mine);
        var session = Derive(mine, serverNonce, serverKey, myKey, isServer: false)
            ?? throw new InvalidOperationException("The other computer sent a key that cannot be used.");
        var hello = new JsonObject { ["type"] = "key", ["ecdh"] = myKey }.ToJsonString();
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, cancel);
        Attach(socket, session);
    }

    private static SecureSession? Derive(ECDiffieHellman mine, string serverNonce, string serverPublicKey, string clientPublicKey, bool isServer)
    {
        try
        {
            string theirs = isServer ? clientPublicKey : serverPublicKey;
            byte[] serverKeyBytes = Convert.FromBase64String(serverPublicKey), clientKeyBytes = Convert.FromBase64String(clientPublicKey);
            if (serverKeyBytes.Length is < 60 or > 100 || clientKeyBytes.Length is < 60 or > 100) return null;
            using var other = ECDiffieHellman.Create();
            other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(theirs), out _);
            if (other.KeySize != 256) return null;
            byte[] secret = mine.DeriveRawSecretAgreement(other.PublicKey);
            byte[] transcript = SHA256.HashData([.. Encoding.ASCII.GetBytes("SwitchMonitor/channel/3"), .. serverKeyBytes, .. clientKeyBytes, .. Encoding.UTF8.GetBytes(serverNonce)]);
            byte[] material = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 72, transcript, Encoding.ASCII.GetBytes("SwitchMonitor keys"));
            byte[] toServer = material[..32], toClient = material[32..64], toServerPrefix = material[64..68], toClientPrefix = material[68..72];
            string binding = Convert.ToBase64String(transcript);
            return isServer
                ? new SecureSession(toClient, toClientPrefix, toServer, toServerPrefix, binding)
                : new SecureSession(toServer, toServerPrefix, toClient, toClientPrefix, binding);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    // ---- Sending and receiving one message ----

    /// <summary>One message: a binary encrypted frame when the connection has keys, a text frame before that.</summary>
    public static Task SendAsync(WebSocket socket, string json, CancellationToken cancel = default) =>
        Of(socket) is { } session
            ? socket.SendAsync(session.Encrypt(json), WebSocketMessageType.Binary, true, cancel)
            : socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancel);

    /// <summary>
    /// The next message as text. Null when the other side closed, the message is too long, a plain message arrives after the keys
    /// were exchanged, or an encrypted one fails its check (the caller closes the connection).
    /// <paramref name="allowPlain"/> lets a plain message through even with no keys (the browser page, when the person allowed it);
    /// without it, only the first "key" message may be plain.
    /// </summary>
    public static async Task<string?> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken cancel, bool allowPlain = true)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > maxBytes) return null;
            if (!result.EndOfMessage) continue;
            var session = Of(socket);
            if (session is null) return result.MessageType == WebSocketMessageType.Text && allowPlain ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) : null;
            return result.MessageType == WebSocketMessageType.Binary ? session.Decrypt(message.GetBuffer().AsSpan(0, (int)message.Length)) : null;
        }
    }
}
