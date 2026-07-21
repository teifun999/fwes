# Installationsanleitung

## Voraussetzungen

**Windows-PC:**
- Windows 10 (Version 1903+) oder Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- LDPlayer und/oder BlueStacks bereits installiert
- Optional, aber empfohlen: [Android Platform-Tools (ADB)](https://developer.android.com/tools/releases/platform-tools)
  im PATH, oder das ADB, das LDPlayer/BlueStacks bereits mitbringt
- Optional (nur für den Windows-Graphics-Capture-Fallback ohne ADB): [ffmpeg](https://ffmpeg.org/download.html)
  im PATH

**iOS-Gerät:**
- iPhone/iPad mit iOS 18 oder neuer
- Xcode 16+ auf einem Mac, um die App zu bauen (Apple erlaubt keine Installation ohne
  Code-Signierung; für den persönlichen Gebrauch reicht eine kostenlose Apple-ID mit
  7-Tage-Signatur oder ein Entwicklerkonto für dauerhafte Installation)

---

## 1. Windows-Server einrichten

### 1.1 Projekt bauen

```powershell
cd Windows\RemoteControlServer
dotnet restore
dotnet build -c Release
```

### 1.2 ADB-Verfügbarkeit prüfen (empfohlen)

```powershell
adb devices
```

Falls `adb` nicht gefunden wird, aber LDPlayer/BlueStacks installiert ist, funktioniert die App
trotzdem über den Windows-Input-Fallback – für beste Performance und Zuverlässigkeit wird jedoch
empfohlen, die Android Platform-Tools separat zu installieren und dem PATH hinzuzufügen.

### 1.3 Firewall

Der Server benötigt eingehende Verbindungen auf:
- **TCP 7887** (WebSocket-Server)
- **UDP 47887** (Netzwerk-Discovery)

Windows fragt beim ersten Start automatisch nach einer Firewall-Freigabe; bei manueller
Konfiguration:

```powershell
New-NetFirewallRule -DisplayName "RemoteEmuControl WS" -Direction Inbound -Protocol TCP -LocalPort 7887 -Action Allow
New-NetFirewallRule -DisplayName "RemoteEmuControl Discovery" -Direction Inbound -Protocol UDP -LocalPort 47887 -Action Allow
```

### 1.4 Starten

```powershell
dotnet run -c Release
```

Ein Tray-Icon erscheint. Rechtsklick → **"Show pairing QR code / PIN"** zeigt den QR-Code und
die aktuelle 6-stellige PIN.

> **Autostart einrichten:** Verknüpfung zur `RemoteControlServer.exe` (unter
> `bin\Release\net8.0-windows10.0.19041.0\`) in den Ordner
> `%AppData%\Microsoft\Windows\Start Menu\Programs\Startup` legen, damit der Server bei
> Windows-Anmeldung automatisch startet.

---

## 2. iOS-App einrichten

Da Apple keine `.xcodeproj`-Binärdatei als reinen Text erzeugen lässt, wird die App als neues
Xcode-Projekt angelegt und die mitgelieferten Swift-Dateien werden importiert:

### 2.1 Xcode-Projekt erstellen

1. Xcode öffnen → **File → New → Project**
2. **iOS → App** wählen
3. Product Name: `RemoteControlApp`, Interface: **SwiftUI**, Language: **Swift**
4. Minimum Deployment: **iOS 18.0**

### 2.2 Quelldateien importieren

Den kompletten Ordner `iOS/RemoteControlApp/` (App, Models, ViewModels, Views, Services) per
Drag & Drop in den Xcode-Projektnavigator ziehen. Bei "Copy items if needed" **aktivieren**.

### 2.3 Info.plist-Einträge ergänzen

Die Datei `iOS/RemoteControlApp/Info.plist` enthält alle notwendigen Zusatzeinträge
(Kamera-Berechtigung für QR-Scan, lokales Netzwerk, ATS-Ausnahme für `ws://`). Diese Keys in
die vom Xcode-Projekt generierte Info.plist übertragen (oder unter **Target → Info** direkt
als Zeilen ergänzen):

- `NSLocalNetworkUsageDescription`
- `NSBonjourServices`
- `NSCameraUsageDescription`
- `NSAppTransportSecurity` → `NSAllowsLocalNetworking = YES`

### 2.4 Signierung

**Target → Signing & Capabilities:**
- Team: eigene Apple-ID auswählen
- Bundle Identifier: z. B. `com.deinname.remotecontrolapp` (muss eindeutig sein)

### 2.5 Auf dem iPhone installieren

1. iPhone per Kabel anschließen, als Build-Ziel auswählen
2. **⌘R** (Build & Run)
3. Auf dem iPhone: **Einstellungen → Allgemein → VPN & Geräteverwaltung** → dem Entwickler-
   Zertifikat vertrauen (nur beim ersten Mal nötig)

---

## 3. Pairing durchführen

1. iOS-App öffnen → landet automatisch auf dem Pairing-Screen und sucht per UDP-Broadcast im
   WLAN nach Servern
2. **Entweder:** gefundenen PC in der Liste antippen (falls `requiresPairing` angezeigt wird,
   erscheint automatisch eine PIN-Abfrage)
3. **Oder:** "QR-Code scannen" antippen und den Code aus dem Windows-Tray-Fenster scannen
4. **Oder (bei anderem Subnetz/Internet-Zugriff):** "Manuell eingeben" mit PC-IP, Port `7887`
   und der PIN

Nach erfolgreichem Pairing merkt sich sowohl die App (Geräte-ID) als auch der Server
(`trusted_devices.json`) die Kopplung dauerhaft – zukünftige Verbindungen benötigen keine
erneute PIN-Eingabe mehr.

---

## 4. Internet-Zugriff (optional, außerhalb des Heimnetzes)

Zwei Wege, in Empfehlungsreihenfolge:

### Option A – VPN (empfohlen)
[Tailscale](https://tailscale.com) oder WireGuard auf dem Windows-PC und dem iPhone
installieren. Beide Geräte erhalten eine virtuelle IP im selben privaten VPN-Netz; die iOS-App
verbindet sich dann per "Manuell eingeben" über diese VPN-IP, exakt wie im lokalen Netzwerk –
ohne dass am Router irgendein Port geöffnet werden muss.

### Option B – Portweiterleitung
Im Router TCP-Port 7887 auf die lokale IP des PCs weiterleiten. **Wichtig:** Siehe
[`SECURITY.md`](./SECURITY.md) – bei dieser Option wird dringend empfohlen, den Transport
zusätzlich per `wss://` (z. B. via Caddy-Reverse-Proxy mit Let's-Encrypt-Zertifikat) abzusichern,
statt sich ausschließlich auf die Anwendungsebenen-Verschlüsselung zu verlassen.

---

## 5. Fehlerbehebung

| Problem | Lösung |
|---|---|
| Server wird im WLAN nicht gefunden | Firewall-Regeln prüfen (Schritt 1.3), sicherstellen dass iPhone & PC im selben WLAN sind (nicht "Gäste"-WLAN mit Client-Isolation) |
| "Kein Emulator gefunden" | LDPlayer/BlueStacks mindestens einmal manuell gestartet haben, damit die Installationspfade erkannt werden; `adb devices` prüfen |
| Ruckelndes Bild / hohe Latenz | Codec auf H.264 statt HEVC stellen (geringere Encoder-Last), Auflösung auf 720p reduzieren, WLAN statt Mobilfunk nutzen |
| Touch-Eingaben kommen nicht an | Prüfen ob ADB verfügbar ist (`adb devices`); falls nicht, sicherstellen dass der Server als Administrator läuft (Pointer-Injection-Fallback benötigt das) |
| App zeigt "PIN ungültig" | PIN im Tray-Fenster neu generieren lassen (PIN rotiert und der QR-Code muss ggf. neu gescannt werden) |
