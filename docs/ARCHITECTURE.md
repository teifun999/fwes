# Architektur

## Überblick

```
┌─────────────────────────┐        WebSocket (ws://)        ┌──────────────────────────────┐
│        iOS App          │ <──────────────────────────────>│      Windows Server           │
│  SwiftUI / MVVM / Combine│    AES-256-GCM verschlüsselt    │  C# .NET 8 / WPF-Tray          │
└─────────────────────────┘                                  └──────────────────────────────┘
                                                                        │
                                                          ┌─────────────┼─────────────┐
                                                          ▼             ▼             ▼
                                                   EmulatorManager  AdbClient   InputSimulator
                                                          │             │             │
                                             ┌────────────┼─────┐       │       (SendInput/
                                             ▼            ▼      ▼      ▼        Pointer API)
                                        LDPlayerPlugin BlueStacksPlugin GenericAdbPlugin
                                             │            │             │
                                             ▼            ▼             ▼
                                        ldconsole.exe  HD-Player.exe  adb (MuMu/Nox/MEmu/...)
```

## Windows-Server – Schichten

### 1. Core (`Core/`)
- **`ProtocolMessages.cs`** – Alle Wire-DTOs. Einzige Quelle der Wahrheit für das Protokoll;
  die iOS-Seite spiegelt diese Typen 1:1 in `ProtocolModels.swift`.
- **`EncryptionService.cs`** – ECDH (P-256) Schlüsselaustausch + HKDF-SHA256 + AES-256-GCM.
  Pro Client-Session wird eine eigene Instanz gehalten (`ClientSession.Crypto`).
- **`PairingService.cs`** – PIN-Generierung/-Validierung, Geräte-Vertrauensspeicher
  (`trusted_devices.json`), QR-Code-Erzeugung (QRCoder-Bibliothek).
- **`WebSocketServer.cs`** – Fleck-basierter WebSocket-Server. Verwaltet `ClientSession` pro
  Verbindung, routet eingehende Envelopes an die passenden Subsysteme.

### 2. Emulatoren (`Emulators/`) – Plugin-Architektur

Jeder Emulator implementiert `IEmulatorPlugin`:

```csharp
public interface IEmulatorPlugin
{
    string EmulatorType { get; }
    bool IsInstalled();
    Task<List<EmulatorInstance>> DiscoverInstancesAsync();
    Task StartAsync(string instanceId);
    Task StopAsync(string instanceId);
    Task RestartAsync(string instanceId);
    IntPtr? GetWindowHandle(string instanceId);
}
```

**Neuen Emulator hinzufügen:** Eine neue Klasse erstellen, die `IEmulatorPlugin` implementiert,
und sie in `EmulatorManager`'s Konstruktor registrieren. Weder `WebSocketServer.cs` noch die
iOS-App müssen angepasst werden – beide sprechen nur mit der emulator-agnostischen
`EmulatorInfo`-DTO.

- **`LDPlayerPlugin`**: Nutzt `ldconsole.exe list2/launch/quit/reboot`. ADB-Port wird aus der
  Instanz-Index-Konvention (`5555 + index*2`) abgeleitet.
- **`BlueStacksPlugin`**: Parst `bluestacks.conf` für Instanznamen + ADB-Ports, startet/stoppt
  über den `HD-Player.exe`-Prozess pro Instanz.
- **`GenericAdbPlugin`**: Fallback für alles, was ADB sieht, aber (noch) kein dediziertes Plugin
  hat (MuMu, Nox, MEmu, Genymotion, ...). Erkennt die Marke über
  `getprop ro.product.manufacturer`. Lifecycle-Steuerung (Start/Stop) ist hier bewusst nicht
  implementiert, da das Installationslayout unbekannt ist – nur Touch/Tasten/ADB-Befehle
  funktionieren, bis ein dediziertes Plugin ergänzt wird.

`EmulatorManager` pollt alle Plugins alle 3 Sekunden, merged die Ergebnisse zu einer flachen
Liste und benachrichtigt verbundene Clients über Statusänderungen.

### 3. Steuerung – zwei Pfade

**Bevorzugt: ADB (`Adb/AdbClient.cs`)**
- `input tap/swipe/keyevent/text` für Touch/Tasten/Text
- `exec-out screencap -p` für Screenshots ohne Zwischenspeicherung auf dem Gerät
- `exec-out screenrecord --output-format=h264 -` für das Live-Video (siehe unten)

**Fallback: Windows-Input (`Input/InputSimulator.cs`)**
- Einzel-Touch: `SetCursorPos` + `mouse_event` gegen das Emulator-Fenster (Koordinaten werden
  über `GetClientRect`/`ClientToScreen` aus normalisierten 0..1-Koordinaten berechnet)
- Echtes Multi-Touch (Pinch-Zoom, Mehrfinger-Wischen): Windows **Pointer Injection API**
  (`InitializeTouchInjection` + `InjectTouchInput`), die bis zu 10 gleichzeitige Kontaktpunkte
  unterstützt. LDPlayer und BlueStacks übersetzen injizierte Pointer-Kontakte korrekt in
  Android-Multi-Touch-Events.
- Tasten: `PostMessage` mit `WM_KEYDOWN`/`WM_KEYUP`/`WM_CHAR` gegen das Zielfenster.

### 4. Video-Pipeline (`Capture/ScreenCaptureService.cs`)

**Pfad A – ADB-Screenrecord (bevorzugt, niedrigste Latenz):**
`adb exec-out screenrecord --output-format=h264 --time-limit 180 -` liefert rohe H.264-Annex-B-
NAL-Units direkt vom Android-Framework-Encoder. Der Prozess wird alle 180s automatisch neu
gestartet (Android-Limit pro Aufruf) und die Chunks werden 1:1 als `stream.frame`-Nachrichten
weitergereicht.

**Pfad B – Windows Graphics Capture + ffmpeg (Fallback ohne ADB):**
1. `WgcFrameGrabber` erfasst das Emulator-Fenster als BGRA-Frames über die
   Windows.Graphics.Capture-API (WinRT-Interop; vollständige Implementierung erfordert
   generierte CsWin32-Bindings, siehe `NativeMethods.txt`-Hinweis im Code).
2. Frames werden roh in `ffmpeg`'s stdin gepumpt (`-f rawvideo -pix_fmt bgra`).
3. `ffmpeg` encodiert mit `libx264`/`hevc_nvenc`, `-preset ultrafast -tune zerolatency` für
   minimale Latenz, und schreibt Annex-B-H.264/HEVC nach stdout.
4. Der Server liest ffmpeg's stdout und leitet die Chunks genauso wie Pfad A weiter.

Auf der iOS-Seite decodiert `VideoDecoderService.swift` beide Codecs hardware-beschleunigt über
**VideoToolbox** (`VTDecompressionSession`), was CPU-Last niedrig hält und die <50ms-LAN-Latenz-
Vorgabe realistisch erreichbar macht.

### 5. Discovery (`Utils/NetworkDiscoveryService.cs`)

UDP-Broadcast-Responder auf Port 47887 (bewusst kein mDNS/Bonjour, da viele Consumer-Router mit
Client-Isolation Multicast-DNS blockieren). Die iOS-App sendet einen Broadcast-Request; jeder
laufende Server antwortet mit Hostname, Port, `serverId` und Pairing-Status.

## iOS-App – Schichten (MVVM)

```
Views/           SwiftUI-Bildschirme (rein deklarativ, keine Business-Logik)
ViewModels/       ObservableObject-Klassen, @Published State, Kommunikation mit Services
Services/         WebSocket, Verschlüsselung, Discovery, Video-Decoding, Input-Versand
Models/           Codable-Structs, die 1:1 die C#-DTOs spiegeln
```

- **`ConnectionViewModel`** ist die zentrale Quelle der Wahrheit (Environment-Object), hält
  Verbindungsstatus, Emulator-Liste, Stats und den dekodierten Video-Frame.
- **`WebSocketService`** kapselt `URLSessionWebSocketTask`, Handshake/Pairing-Ablauf und
  automatischen Reconnect mit exponentiellem Backoff (max. 30s).
- **`ControlOverlayView`** nutzt bewusst eine UIKit-`UIView`-Brücke (`TouchCaptureView`) statt
  reiner SwiftUI-Gesten, da SwiftUI keine stabilen Multi-Touch-Kontakt-IDs über
  `MagnificationGesture`/`DragGesture` hinaus liefert – für echtes Pinch-Zoom und
  Mehrfinger-Wischen wird direkter `UITouch`-Zugriff benötigt.

## Erweiterbarkeit

- **Neuer Emulator:** neues `IEmulatorPlugin` + Registrierung in `EmulatorManager`.
- **Neuer Video-Codec:** `StreamConfigPayload.Codec` erweitern, entsprechenden ffmpeg-Encoder-
  Namen in `ScreenCaptureService.StreamViaWindowCaptureAsync` ergänzen, iOS-seitig
  `VideoDecoderService` um die passende `CMVideoFormatDescriptionCreateFrom...ParameterSets`-
  Variante ergänzen.
- **Neue Eingabeart:** `MessageType`-Konstante + DTO in `ProtocolMessages.cs`/
  `ProtocolModels.swift` ergänzen, Routing-Case in `WebSocketServer.RouteAuthenticatedMessage`
  und passenden Sender in `InputSenderService.swift`.
