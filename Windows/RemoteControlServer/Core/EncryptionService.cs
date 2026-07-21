using System;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace RemoteControlServer.Core;

/// <summary>
/// Handles the cryptographic side of the protocol:
///  1. ECDH (P-256) key exchange during pairing to derive a shared secret.
///  2. HKDF to turn the shared secret into an AES-256 session key.
///  3. AES-256-GCM authenticated encryption/decryption for every message after pairing.
///
/// One instance of this class is created per connected client (per session), since each
/// client negotiates its own session key.
/// </summary>
public class EncryptionService : IDisposable
{
    private ECDiffieHellman? _serverEcdh;
    private byte[]? _sessionKey; // 32 bytes, AES-256

    public bool IsSessionEstablished => _sessionKey != null;

    /// <summary>Generates the server's ephemeral ECDH keypair and returns the public key to send to the client.</summary>
    public byte[] GenerateServerKeyPair()
    {
        _serverEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return _serverEcdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Derives the shared AES-256 session key from the client's public key using ECDH + HKDF-SHA256.
    /// Must be called after GenerateServerKeyPair().
    /// </summary>
    public void DeriveSessionKey(byte[] clientPublicKeySpki, string salt)
    {
        if (_serverEcdh == null)
            throw new InvalidOperationException("Call GenerateServerKeyPair() first.");

        using var clientKey = ECDiffieHellman.Create();
        clientKey.ImportSubjectPublicKeyInfo(clientPublicKeySpki, out _);

        byte[] sharedSecret = _serverEcdh.DeriveKeyMaterial(clientKey.PublicKey);

        byte[] saltBytes = System.Text.Encoding.UTF8.GetBytes(salt);
        byte[] infoBytes = System.Text.Encoding.UTF8.GetBytes("RemoteEmuControl-session-key-v1");

        // HKDF-Expand to get a well-distributed 32-byte AES-256 key.
        // NOTE: positional arguments are used deliberately - HKDF.DeriveKey has both a byte[]-based
        // and a Span<byte>-based overload, and named arguments can resolve to the "wrong" one
        // depending on the compiler/SDK version, producing a confusing CS1739. Positional calls
        // avoid that ambiguity entirely.
        _sessionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, saltBytes, infoBytes);
    }

    /// <summary>Encrypts a payload object into an Envelope-ready (iv, tag, base64 ciphertext) tuple.</summary>
    public (string ivBase64, string tagBase64, string payloadBase64) Encrypt<T>(T payload)
    {
        if (_sessionKey == null)
            throw new InvalidOperationException("Session key not established.");

        string json = JsonConvert.SerializeObject(payload);
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(json);

        byte[] nonce = RandomNumberGenerator.GetBytes(12); // 96-bit GCM nonce
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];

        using var aesGcm = new AesGcm(_sessionKey, 16);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        return (Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
    }

    /// <summary>Decrypts an Envelope's payload back into the requested DTO type.</summary>
    public T Decrypt<T>(Envelope envelope)
    {
        if (_sessionKey == null)
            throw new InvalidOperationException("Session key not established.");
        if (envelope.Iv == null || envelope.Tag == null)
            throw new ArgumentException("Envelope missing iv/tag - was it actually encrypted?");

        byte[] nonce = Convert.FromBase64String(envelope.Iv);
        byte[] tag = Convert.FromBase64String(envelope.Tag);
        byte[] ciphertext = Convert.FromBase64String(envelope.Payload);
        byte[] plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_sessionKey, 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        string json = System.Text.Encoding.UTF8.GetString(plaintext);
        return JsonConvert.DeserializeObject<T>(json)!;
    }

    public void Dispose()
    {
        _serverEcdh?.Dispose();
        if (_sessionKey != null)
            CryptographicOperations.ZeroMemory(_sessionKey);
    }
}
