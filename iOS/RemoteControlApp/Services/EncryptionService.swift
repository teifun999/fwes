import Foundation
import CryptoKit

/// Mirrors RemoteControlServer.Core.EncryptionService: performs the client side of the ECDH
/// (P-256) key exchange during pairing, derives an AES-256 session key via HKDF, and then
/// encrypts/decrypts every subsequent message with AES-256-GCM.
final class EncryptionService {
    private var privateKey: P256.KeyAgreement.PrivateKey?
    private var sessionKey: SymmetricKey?

    var isSessionEstablished: Bool { sessionKey != nil }

    /// Generates this device's ephemeral ECDH keypair; the public key (SPKI/X9.63 form) is sent
    /// to the server inside PairRequestPayload.clientPublicKey.
    func generateClientKeyPair() -> Data {
        let key = P256.KeyAgreement.PrivateKey()
        privateKey = key
        return key.publicKey.derRepresentation
    }

    /// Derives the shared AES-256 session key from the server's public key, using the same
    /// HKDF salt/info strings as the C# side so both ends land on an identical key.
    func deriveSessionKey(serverPublicKeyData: Data, salt: String) throws {
        guard let privateKey else {
            throw EncryptionError.missingKeyPair
        }
        let serverPublicKey = try P256.KeyAgreement.PublicKey(derRepresentation: serverPublicKeyData)
        let sharedSecret = try privateKey.sharedSecretFromKeyAgreement(with: serverPublicKey)

        let derivedKey = sharedSecret.hkdfDerivedSymmetricKey(
            using: SHA256.self,
            salt: Data(salt.utf8),
            sharedInfo: Data("RemoteEmuControl-session-key-v1".utf8),
            outputByteCount: 32
        )
        sessionKey = derivedKey
    }

    /// Encrypts a Codable payload, returning (ivBase64, tagBase64, ciphertextBase64) exactly
    /// like the C# side's tuple, ready to drop into an Envelope.
    func encrypt<T: Encodable>(_ payload: T) throws -> (iv: String, tag: String, payload: String) {
        guard let sessionKey else { throw EncryptionError.noSessionKey }

        let jsonData = try JSONEncoder.remoteControl.encode(payload)
        let nonce = AES.GCM.Nonce()
        let sealedBox = try AES.GCM.seal(jsonData, using: sessionKey, nonce: nonce)

        return (
            iv: Data(sealedBox.nonce).base64EncodedString(),
            tag: sealedBox.tag.base64EncodedString(),
            payload: sealedBox.ciphertext.base64EncodedString()
        )
    }

    /// Decrypts an Envelope's payload back into the requested Decodable type.
    func decrypt<T: Decodable>(_ envelope: Envelope, as type: T.Type) throws -> T {
        guard let sessionKey else { throw EncryptionError.noSessionKey }
        guard let ivB64 = envelope.iv, let tagB64 = envelope.tag,
              let ivData = Data(base64Encoded: ivB64),
              let tagData = Data(base64Encoded: tagB64),
              let cipherData = Data(base64Encoded: envelope.payload) else {
            throw EncryptionError.malformedEnvelope
        }

        let nonce = try AES.GCM.Nonce(data: ivData)
        let sealedBox = try AES.GCM.SealedBox(nonce: nonce, ciphertext: cipherData, tag: tagData)
        let plaintext = try AES.GCM.open(sealedBox, using: sessionKey)

        return try JSONDecoder.remoteControl.decode(type, from: plaintext)
    }
}

enum EncryptionError: Error {
    case missingKeyPair
    case noSessionKey
    case malformedEnvelope
}

extension JSONEncoder {
    static let remoteControl: JSONEncoder = {
        JSONEncoder()
    }()
}

extension JSONDecoder {
    static let remoteControl: JSONDecoder = {
        JSONDecoder()
    }()
}
