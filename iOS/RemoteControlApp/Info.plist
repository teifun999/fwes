<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<!--
  This is a REFERENCE Info.plist. When creating the Xcode project (File > New > Project >
  App, iOS 18, SwiftUI, name "RemoteControlApp"), Xcode generates its own Info.plist / target
  settings UI. Copy the keys below into it (or paste this file's contents in if you use
  "Generate Info.plist file" = No and manage it manually).
-->
<plist version="1.0">
<dict>
    <key>CFBundleDisplayName</key>
    <string>RemoteEmuControl</string>

    <!-- Required: local network + Bonjour/UDP discovery of the Windows server -->
    <key>NSLocalNetworkUsageDescription</key>
    <string>Wird benötigt, um deinen Windows-PC im lokalen Netzwerk zu finden.</string>
    <key>NSBonjourServices</key>
    <array>
        <string>_remoteemucontrol._tcp</string>
    </array>

    <!-- Required: QR-code pairing scan -->
    <key>NSCameraUsageDescription</key>
    <string>Wird benötigt, um den Pairing-QR-Code deines PCs zu scannen.</string>

    <!-- Required: push notifications for "connection lost" alerts -->
    <key>UIBackgroundModes</key>
    <array>
        <string>remote-notification</string>
    </array>

    <!-- App Transport Security: allow local ws:// (non-TLS) traffic to the paired PC.
         The payload itself is already AES-256-GCM encrypted at the application layer (see
         EncryptionService.swift), so plain ws:// is acceptable for LAN use; for internet-exposed
         setups (port forwarding/VPN) switching the transport to wss:// with a real certificate
         is recommended - see INSTALL.md "Internet-Zugriff" section. -->
    <key>NSAppTransportSecurity</key>
    <dict>
        <key>NSAllowsLocalNetworking</key>
        <true/>
        <key>NSExceptionDomains</key>
        <dict/>
    </dict>

    <key>UISupportedInterfaceOrientations</key>
    <array>
        <string>UIInterfaceOrientationPortrait</string>
        <string>UIInterfaceOrientationLandscapeLeft</string>
        <string>UIInterfaceOrientationLandscapeRight</string>
    </array>

    <key>UIRequiresFullScreen</key>
    <true/>
</dict>
</plist>
