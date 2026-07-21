# Sicherheitsmodell

## Verschlüsselung

- **Schlüsselaustausch:** ECDH auf der P-256-Kurve. Client und Server generieren je ein
  ephemeres Schlüsselpaar pro Pairing-Vorgang (Perfect Forward Secrecy – ein kompromittierter
  Schlüssel aus einer alten Sitzung erlaubt keine Entschlüsselung neuer Sitzungen).
- **Schlüsselableitung:** HKDF-SHA256 mit der `deviceId` als Salt und einem festen Info-String
  (`"RemoteEmuControl-session-key-v1"`), um aus dem ECDH-Shared-Secret einen wohlverteilten
  256-Bit-AES-Schlüssel zu erzeugen.
- **Verschlüsselung:** AES-256-GCM (authentifizierte Verschlüsselung). Jede Nachricht erhält
  eine frische 96-Bit-Nonce; der 128-Bit-Auth-Tag verhindert unbemerkte Manipulation.

## Pairing-Ablauf

1. **PIN:** Der Server zeigt eine rotierende 6-stellige PIN im Tray-Fenster an
   (`PairingService.GeneratePin()`). Die PIN kann jederzeit manuell neu generiert werden.
2. **QR-Code:** Kodiert `host`, `port`, `serverId` und die aktuelle PIN als
   `remoteemu://pair?...`-URI – ein Scan ersetzt die manuelle PIN-Eingabe vollständig.
3. **Gerätevertrauen:** Nach erfolgreichem Pairing wird die `deviceId` des iPhones dauerhaft in
   `trusted_devices.json` gespeichert (`%AppData%\RemoteEmuControl\`). Zukünftige Verbindungen
   von dieser `deviceId` benötigen keine erneute PIN-Eingabe, führen aber bei jeder neuen
   WebSocket-Verbindung dennoch einen frischen ECDH-Austausch durch (neuer Session-Key pro
   Verbindung, nicht nur pro Gerät).
4. **Widerruf:** `PairingService.RevokeDevice(deviceId)` entfernt ein Gerät aus der
   Vertrauensliste (z. B. bei Geräteverlust) – aktuell nur programmatisch, eine UI dafür kann
   leicht im Tray-Menü ergänzt werden.

## Bedrohungsmodell und Annahmen

- Das Protokoll läuft über **ws:// (unverschlüsseltes WebSocket-Transport-Layer)**, aber jede
  Payload ist bereits auf Anwendungsebene AES-256-GCM-verschlüsselt. Das ist für den
  LAN-Einsatz ausreichend sicher, da ein Angreifer im selben Netzwerk zwar die Frames sieht,
  aber ohne Session-Key nichts entschlüsseln kann.
- **Für Internet-Exposition (Portweiterreichung) wird dringend empfohlen**, den WebSocket-
  Transport zusätzlich hinter TLS zu betreiben (`wss://`) – z. B. über einen Reverse-Proxy
  (Caddy/nginx) mit einem echten Zertifikat, oder besser: ausschließlich über ein VPN
  (WireGuard/Tailscale) zugänglich zu machen, statt Ports direkt am Router zu öffnen.
  Die Kombination "nur PIN + eigene Verschlüsselung, offen ins Internet" ist ausreichend für
  Heimnetz-Nutzung, aber kein Ersatz für ein VPN bei Fernzugriff.
- **PIN-Rotation:** Da die PIN nur beim initialen Pairing gebraucht wird und danach die
  `deviceId` vertraut wird, sollte die PIN nicht dauerhaft sichtbar bleiben – die Tray-App zeigt
  sie nur auf Anfrage/in einem eigenen Fenster an.

## Rechteanforderungen (Windows)

Die App fordert standardmäßig `requireAdministrator` (siehe `app.manifest`), da `SendInput`/
Pointer-Injection zuverlässig nur funktioniert, wenn der eigene Prozess mindestens das gleiche
Rechteniveau wie das Zielfenster hat – LDPlayer und BlueStacks laufen häufig erhöht. Wer ADB
exklusiv nutzt (empfohlen) und nie in den Windows-Input-Fallback fällt, kann das Manifest auf
`asInvoker` zurücksetzen.
