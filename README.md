# RemoteEmuControl

Professionelle Remote-Control-Lösung zur vollständigen Fernsteuerung von Android-Emulatoren
(LDPlayer, BlueStacks und automatisch erkannte weitere Emulatoren) von einer iOS-App aus.

```
RemoteEmuControl/
├── Windows/RemoteControlServer/   # C# .NET 8 / WPF Server (läuft auf dem PC)
├── iOS/RemoteControlApp/          # SwiftUI iOS-App (iOS 18+)
└── docs/                          # Architektur, Protokoll, Installation
```

## Kernidee

Weder LDPlayer noch BlueStacks bieten eine offizielle Fernsteuerungs-API. Diese Lösung löst
das über zwei Ebenen:

1. **ADB (Android Debug Bridge)** – bevorzugter Pfad, wenn verfügbar. Funktioniert
   emulatorunabhängig, ist pixelgenau und benötigt kein Fensterfokus.
2. **Windows-Input-Simulation (SendInput / Pointer Injection)** – Fallback, falls kein ADB
   verfügbar ist. Steuert die Emulator-Fenster direkt über Maus-/Tastatur-/Touch-Injection.

Beide Pfade sind hinter derselben Abstraktion (`IEmulatorPlugin`) versteckt, sodass die iOS-App
und das Protokoll komplett emulatorunabhängig bleiben.

## Features im Überblick

| Bereich | Umsetzung |
|---|---|
| Verbindung | WLAN-Auto-Discovery (UDP-Broadcast), QR-Code-Pairing, manuelle IP-Eingabe, PIN + AES-256-GCM |
| Video | H.264/HEVC-Streaming via `adb screenrecord` (bevorzugt) oder Windows Graphics Capture + ffmpeg |
| Steuerung | Touch, Multi-Touch/Pinch, Swipe, Doppeltipp, Long-Press, virtuelle Tastatur |
| Emulator-Tasten | Home, Zurück, Menü, Letzte Apps, Lautstärke, Power, Screenshot, Drehen |
| Verwaltung | Mehrere Emulatoren gleichzeitig, Start/Stop/Neustart, Live-Wechsel |
| Extras | Makro-Aufnahme/-Wiedergabe, eigene Tastenbelegung, Dateiübertragung, Zwischenablage-Sync |

## Schnellstart

Siehe [`docs/INSTALL.md`](../docs/INSTALL.md) für die vollständige Installationsanleitung.

1. Windows-Server bauen & starten (`dotnet run` im `Windows/RemoteControlServer`-Ordner)
2. iOS-App in Xcode aus den Quellen unter `iOS/RemoteControlApp` als neues SwiftUI-Projekt anlegen
3. QR-Code vom Server-Tray-Icon mit der App scannen → fertig gekoppelt

## Dokumentation

- [`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) – Systemarchitektur, Datenfluss, Erweiterbarkeit
- [`docs/PROTOCOL.md`](../docs/PROTOCOL.md) – vollständige Wire-Format-Referenz
- [`docs/INSTALL.md`](../docs/INSTALL.md) – Schritt-für-Schritt-Installation (Windows + iOS)
- [`docs/BUILD_WITHOUT_MAC.md`](../docs/BUILD_WITHOUT_MAC.md) – iOS-App bauen & installieren **ohne eigenen Mac** (GitHub Actions + Sideloadly)
- [`docs/SECURITY.md`](../docs/SECURITY.md) – Sicherheitsmodell, Verschlüsselung, Pairing
