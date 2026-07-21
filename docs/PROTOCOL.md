# Kommunikationsprotokoll

Vollständige Referenz des WebSocket-JSON-Protokolls zwischen iOS-App und Windows-Server.
Quellen der Wahrheit im Code: `Windows/RemoteControlServer/Core/ProtocolMessages.cs` und
`iOS/RemoteControlApp/Models/ProtocolModels.swift` (müssen synchron gehalten werden).

## Transport

- Ein WebSocket pro verbundenem Client, Standardport **7887** (WebSocket-Server) und
  UDP-Port **47887** (Netzwerk-Discovery, separat).
- Jede Nachricht ist ein einzelnes WebSocket-Text-Frame mit folgendem JSON-Envelope:

```json
{
  "v": 1,
  "type": "input.touch",
  "id": "3f9a1c2e-...",
  "ts": 1737249123000,
  "iv": "base64 (12 Byte AES-GCM Nonce)",
  "tag": "base64 (16 Byte AES-GCM Auth-Tag)",
  "payload": "base64 (AES-256-GCM Chiffretext des eigentlichen JSON-Bodys)"
}
```

`iv`/`tag`/verschlüsseltes `payload` sind nur nach erfolgreichem Pairing vorhanden. Die zwei
Handshake-Nachrichten (`hello.*`, `pair.*`) werden unverschlüsselt übertragen (siehe unten).

## Verbindungsaufbau (Sequenzdiagramm)

```
iOS                                            Windows-Server
 │  hello.request (unverschlüsselt)                  │
 │ ──────────────────────────────────────────────────>│
 │                                                     │  prüft, ob deviceId bereits vertraut ist
 │  hello.response { requiresPairing }                │
 │ <──────────────────────────────────────────────────│
 │                                                     │
 │  pair.request { pin, clientPublicKey (ECDH) }       │
 │ ──────────────────────────────────────────────────>│
 │                                                     │  validiert PIN (oder Vertrauensstatus)
 │                                                     │  generiert Server-ECDH-Keypair
 │                                                     │  leitet Session-Key via HKDF ab
 │  pair.response { success, serverPublicKey }         │
 │ <──────────────────────────────────────────────────│
 │  (leitet identischen Session-Key ab)                │
 │                                                     │
 │ ══════ ab hier: alle Nachrichten AES-256-GCM ═══════│
```

## Nachrichtentypen

### Verbindungs-Lifecycle
| Typ | Richtung | Payload | Beschreibung |
|---|---|---|---|
| `hello.request` | Client→Server | `HelloRequestPayload` | Gerätename, deviceId, App-Version |
| `hello.response` | Server→Client | `HelloResponsePayload` | Pairing-Status, unterstützte Codecs |
| `pair.request` | Client→Server | `PairRequestPayload` | PIN + ECDH-Public-Key |
| `pair.response` | Server→Client | `PairResponsePayload` | Erfolg, Server-Public-Key, Session-Token |
| `ping` / `pong` | beide | leer | Keep-Alive + Latenzmessung (alle 3s) |
| `error` | Server→Client | `ErrorPayload` | Fehlercode + Klartext-Meldung |

### Emulator-Verwaltung
| Typ | Richtung | Payload |
|---|---|---|
| `emulator.list` | Client→Server (Request) / Server→Client (Antwort: `EmulatorInfo[]`) |
| `emulator.select` | Client→Server | `EmulatorInfo` (nur `id` relevant) |
| `emulator.start` / `stop` / `restart` | Client→Server | `EmulatorInfo` (nur `id` relevant) |

### Streaming
| Typ | Richtung | Payload |
|---|---|---|
| `stream.start` | Client→Server | `StreamConfigPayload` |
| `stream.stop` | Client→Server | leer |
| `stream.config` | Client→Server | `StreamConfigPayload` (Live-Update ohne Neustart) |
| `stream.frame` | Server→Client | `{ emulatorId, codec, data (base64 NAL-Units), keyFrame }` |
| `stream.stats` | Server→Client | `StreamStatsPayload` (Ping, FPS, Bitrate, CPU%, RAM%) |

### Eingabe
| Typ | Richtung | Payload |
|---|---|---|
| `input.touch` | Client→Server | `TouchEventPayload` (normalisierte x/y 0..1, action: down/move/up/tap/doubletap/longpress) |
| `input.multitouch` | Client→Server | `MultiTouchPayload` (Liste von `TouchPoint` mit id/phase) |
| `input.key` | Client→Server | `KeyEventPayload` (Android-Keycode, down/up) |
| `input.text` | Client→Server | `TextInputPayload` (freier Text für virtuelle Tastatur) |
| `input.button` | Client→Server | `ButtonPayload` (home/back/menu/recents/volumeup/volumedown/power/screenshot/rotate) |

### Sonstiges
| Typ | Richtung | Payload |
|---|---|---|
| `adb.command` | Client→Server | `AdbCommandPayload` (Raw-ADB-Argumente nach `-s <serial>`) |
| `apk.install` | Client→Server | Dateiübertragung + Installationsauftrag |
| `screenshot.request` / `screenshot.response` | beide | `EmulatorInfo` / `{ imageBase64 }` |
| `macro.record.start` / `stop` / `play` / `list` / `save` | Client→Server bzw. lokal (iOS-seitig persistiert) |
| `file.transfer` | beide | Chunked Base64-Dateiübertragung |
| `clipboard.sync` | beide | Text-Payload für geteilte Zwischenablage |

## Koordinatensystem

Alle Touch-/Multi-Touch-Koordinaten sind **normalisiert im Bereich 0.0–1.0**, relativ zur
sichtbaren Bildfläche auf dem iPhone. Der Server skaliert sie serverseitig auf die tatsächliche
Emulator-Auflösung (per `adb shell wm size` bzw. Fenstergröße), sodass Rotation, unterschiedliche
Emulator-Auflösungen und verschiedene iPhone-Displaygrößen korrekt funktionieren, ohne dass der
Client die Zielauflösung kennen muss.

## Sicherheit

Siehe [`SECURITY.md`](./SECURITY.md) für das vollständige Kryptografiemodell.

## Versionierung

Das Feld `v` im Envelope ermöglicht zukünftige Breaking Changes: neue Feldbedeutungen werden nur
für höhere `v`-Werte aktiviert; ältere Client/Server-Kombinationen bleiben kompatibel, solange
nur zusätzliche optionale Felder eingeführt werden (empfohlener Weg für nicht-breaking Änderungen).
