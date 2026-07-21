# iOS-App bauen und installieren – ganz ohne Mac

Diese Anleitung baut die App vollständig in der Cloud über **GitHub Actions** (Apple erlaubt
iOS-Builds nur auf macOS, aber GitHub stellt dafür kostenlose virtuelle Mac-Runner bereit) und
installiert sie anschließend direkt von deinem **Windows-PC** aus auf dein iPhone – ganz ohne
eigenen Mac.

## Überblick

```
Dein Windows-PC                GitHub (Cloud-Mac)              Dein iPhone
     │                                │                              │
     │  1. Code hochladen            │                              │
     ├───────────────────────────────>│                              │
     │                                │  2. App bauen (unsigniert)   │
     │                                │     .ipa-Datei erzeugen      │
     │  3. .ipa herunterladen         │                              │
     │<───────────────────────────────┤                              │
     │  4. Mit Sideloadly signieren   │                              │
     │     & über USB installieren   │                              │
     ├──────────────────────────────────────────────────────────────>│
```

---

## Schritt 1: GitHub-Repository erstellen

1. Auf [github.com](https://github.com) registrieren/anmelden (kostenlos).
2. Oben rechts **"+" → "New repository"**.
3. Name z. B. `RemoteEmuControl`, Sichtbarkeit **Private** (empfohlen, da dein Quellcode sonst
   öffentlich einsehbar ist), **"Create repository"**.

## Schritt 2: Code hochladen

Am einfachsten über die Weboberfläche, kein Git-Kommandozeilen-Wissen nötig:

1. Im leeren Repository auf **"uploading an existing file"** klicken.
2. Den kompletten Inhalt deines entpackten `RemoteEmuControl`-Ordners per Drag & Drop hineinziehen
   (inklusive des `.github`-Ordners – der ist unsichtbar in Windows Explorer, außer du hast
   versteckte Dateien aktiviert unter **Ansicht → Ein-/ausblenden → Versteckte Elemente**).
3. Commit-Nachricht eingeben (z. B. "Initial upload") → **"Commit changes"**.

> **Alternative (empfohlen für spätere Änderungen):** [GitHub Desktop](https://desktop.github.com/)
> installieren – eine grafische Oberfläche für Windows, die das Hochladen/Aktualisieren von
> Dateien viel komfortabler macht als der Browser-Upload.

## Schritt 3: Build automatisch starten lassen

Der Workflow unter `.github/workflows/build-ios.yml` startet automatisch bei jedem Upload zum
`main`-Branch. Du kannst ihn auch manuell anstoßen:

1. Im Repository auf den Reiter **"Actions"** klicken.
2. Links **"Build iOS IPA (unsigned)"** auswählen.
3. Rechts **"Run workflow"** → **"Run workflow"** (grüner Button).
4. Nach ca. 3–6 Minuten zeigt ein grüner Haken ✅, dass der Build erfolgreich war
   (bei einem roten ❌ auf den Lauf klicken, um die Fehlermeldung zu sehen).

## Schritt 4: Die .ipa-Datei herunterladen

1. Auf den erfolgreichen Workflow-Lauf klicken.
2. Ganz unten unter **"Artifacts"** erscheint `RemoteControlApp-unsigned-ipa` → anklicken zum
   Herunterladen (lädt als `.zip`).
3. Die `.zip` entpacken → darin liegt `RemoteControlApp-unsigned.ipa`.

Diese `.ipa` ist noch **unsigniert** – iPhones installieren grundsätzlich nur digital signierte
Apps. Das Signieren übernimmt jetzt Sideloadly auf deinem PC.

## Schritt 5: Sideloadly installieren

1. [sideloadly.io](https://sideloadly.io/) öffnen → Windows-Version herunterladen und installieren.
2. Falls noch nicht vorhanden: Die **Apple Devices App** (im Microsoft Store) oder **iTunes**
   installieren – wird für die USB-Treiber benötigt, damit Windows dein iPhone erkennt.
3. iPhone per Kabel an den PC anschließen, am iPhone **"Diesem Computer vertrauen"** bestätigen.

## Schritt 6: App signieren & installieren

1. Sideloadly öffnen. Dein iPhone sollte oben im Geräte-Dropdown erscheinen.
2. Die heruntergeladene `RemoteControlApp-unsigned.ipa` per Drag & Drop in das Sideloadly-Fenster
   ziehen.
3. Unten bei **"Apple ID"** deine Apple-ID-E-Mail eingeben (ein **App-spezifisches Passwort**
   wird empfohlen, nicht dein normales Passwort – erstellst du unter
   [appleid.apple.com](https://appleid.apple.com) → Anmeldung & Sicherheit →
   App-spezifische Passwörter).
   > Tipp: Nutze wenn möglich eine **zweite/separate Apple-ID** statt deiner Hauptaccount-ID,
   > falls du eine hast – das ist unproblematisch, aber sauberer getrennt.
4. **"Start"** klicken. Sideloadly signiert die App mit einem kostenlosen Entwicklerzertifikat
   deiner Apple-ID und installiert sie direkt aufs iPhone.

## Schritt 7: Der App vertrauen (einmalig)

Nach der Installation meldet iOS zunächst "Nicht vertrauenswürdiger Entwickler":

1. Am iPhone: **Einstellungen → Allgemein → VPN & Geräteverwaltung**
2. Unter "Entwickler-App" deine Apple-ID antippen → **"Vertrauen"** bestätigen
3. App öffnen – funktioniert jetzt normal

## Wichtig: 7-Tage-Limit bei kostenloser Apple-ID

Mit einer **kostenlosen** Apple-ID sind selbst-signierte Apps nur **7 Tage** gültig, danach
stürzt die App beim Öffnen ab und muss neu installiert werden:

- **Einfachster Weg:** Alle 7 Tage Schritt 6 wiederholen (iPhone anschließen, IPA erneut per
  Sideloadly installieren – die `.ipa`-Datei bleibt dieselbe, kein neuer GitHub-Build nötig,
  solange sich der Code nicht geändert hat).
- **Dauerhafte Lösung:** Ein [Apple Developer Program](https://developer.apple.com/programs/)-
  Konto (99 $/Jahr) verlängert die Gültigkeit auf **1 Jahr** statt 7 Tage.

## Alternative: AltStore/AltServer

Statt Sideloadly kannst du auch [AltStore](https://altstore.io/) mit **AltServer für Windows**
nutzen – funktioniert nach demselben Prinzip (Apple-ID-Signierung, 7-Tage-Limit bei kostenloser
ID), bietet aber zusätzlich eine automatische Hintergrund-Erneuerung der Signatur über WLAN,
solange AltServer auf deinem PC läuft und PC + iPhone im selben Netzwerk sind.

## Änderungen am Code

Wenn du später etwas an den Swift-Dateien änderst:
1. Geänderte Datei im GitHub-Repository ersetzen (Browser-Upload oder GitHub Desktop)
2. Der Workflow läuft automatisch erneut (Push auf `main`)
3. Neue `.ipa` unter "Actions" herunterladen und wie in Schritt 6 erneut über Sideloadly
   installieren
